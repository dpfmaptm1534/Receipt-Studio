using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace ReceiptStudio;

public sealed class Receipt : IDisposable
{
    public string Name { get; set; }
    public Bitmap Image { get; set; }
    public int PrintWidthMm { get; set; } // 0 follows the document's default width.
    public DateTime? PaymentDate { get; set; }
    public bool DateEdited { get; set; }
    public string OcrText { get; set; } = "";
    public string OcrStatus { get; set; } = "미인식";
    public List<DateTime> DateCandidates { get; set; } = [];
    public string DateLabel => PaymentDate is {} date ? $"{date:yyyy-MM-dd} · {(DateEdited?"확인됨":"OCR 추정")}" : (DateEdited?"날짜 미지정":OcrStatus);
    public void ClearRecognition() { PaymentDate=null;DateEdited=false;OcrText="";OcrStatus="미인식";DateCandidates=[]; }
    public Receipt(string name, Bitmap image) { Name = name; Image = image; }
    public override string ToString() => Name;
    public void Dispose() => Image.Dispose();
    public static Bitmap Read(string path)
    {
        using var source = System.Drawing.Image.FromFile(path);
        if (source.PropertyIdList.Contains(0x112))
        {
            var value = source.GetPropertyItem(0x112)?.Value;
            if (value is { Length: >= 2 })
                source.RotateFlip(BitConverter.ToUInt16(value, 0) switch {
                    2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone });
        }
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.White); g.DrawImage(source, 0, 0, source.Width, source.Height);
        return bitmap;
    }
}

public sealed class PageSettings
{
    public string Title { get; set; } = "2026년 10월 지출영수증 이재원 사원";
    public bool Landscape { get; set; }
    public int PerPage { get; set; } = 6;
    public int Margin { get; set; } = 10;
    public int Gap { get; set; } = 4;
    public bool Border { get; set; } = true;
    public int ReceiptWidthMm { get; set; } = 60;
    public float Width => Landscape ? 297 : 210;
    public float Height => Landscape ? 210 : 297;
}

public sealed record ReceiptPlacement(Receipt Receipt, RectangleF Bounds, Rectangle Source, int Part=1, int Parts=1);
public sealed class ReceiptPage
{
    public List<ReceiptPlacement> Items { get; } = [];
}

public static class LayoutEngine
{
    public static RectangleF ContentArea(PageSettings s) => new(s.Margin,s.Margin+21,s.Width-2*s.Margin,s.Height-2*s.Margin-30);

    // Physical print widths are never reduced to satisfy the requested receipt count.
    // Pack in reading order, preserving aspect ratio; overflow starts another page.
    public static List<ReceiptPage> Plan(IReadOnlyList<Receipt> receipts, PageSettings settings)
    {
        var pages=new List<ReceiptPage>();var current=new ReceiptPage();
        var area=ContentArea(settings);float x=area.X,y=area.Y,rowHeight=0,gap=settings.Gap;
        void FinishPage()
        {
            if(current.Items.Count>0)pages.Add(current);
            current=new ReceiptPage();x=area.X;y=area.Y;rowHeight=0;
        }
        foreach(var receipt in receipts)
        {
            float width=receipt.PrintWidthMm==0?settings.ReceiptWidthMm:receipt.PrintWidthMm;
            if(width<30 || width>150 || width>area.Width)throw new InvalidDataException("출력 폭은 용지 안에 들어가는 30~150mm로 지정해 주세요.");
            float height=width*receipt.Image.Height/receipt.Image.Width;
            if(height>area.Height+.001f)
            {
                // Extra-long receipts continue on dedicated pages, without shrinking text.
                FinishPage();int pixelsPerPage=Math.Max(1,(int)Math.Floor(area.Height*receipt.Image.Width/width));
                int parts=(receipt.Image.Height+pixelsPerPage-1)/pixelsPerPage;
                for(int top=0,part=1;top<receipt.Image.Height;top+=pixelsPerPage,part++)
                {
                    int pixels=Math.Min(pixelsPerPage,receipt.Image.Height-top);
                    current.Items.Add(new ReceiptPlacement(receipt,new RectangleF(area.X,area.Y,width,width*pixels/receipt.Image.Width),new Rectangle(0,top,receipt.Image.Width,pixels),part,parts));
                    FinishPage();
                }
                continue;
            }
            if(current.Items.Count>=settings.PerPage)FinishPage();
            if(x+width>area.Right+.001f){x=area.X;y+=rowHeight+gap;rowHeight=0;}
            if(y+height>area.Bottom+.001f)FinishPage();
            current.Items.Add(new ReceiptPlacement(receipt,new RectangleF(x,y,width,height),new Rectangle(0,0,receipt.Image.Width,receipt.Image.Height)));
            x+=width+gap;rowHeight=Math.Max(rowHeight,height);
        }
        FinishPage();if(pages.Count==0)pages.Add(new ReceiptPage());return pages;
    }

    public static Bitmap Render(ReceiptPage layout, PageSettings settings, int page, int total, int dpi)
    {
        float scale = dpi / 25.4f;
        var image = new Bitmap((int)Math.Round(settings.Width * scale), (int)Math.Round(settings.Height * scale), PixelFormat.Format24bppRgb);
        image.SetResolution(dpi, dpi);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.White);
        graphics.ScaleTransform(scale, scale);
        Draw(graphics, layout, settings, page, total);
        return image;
    }

    public static void Draw(Graphics g, ReceiptPage layout, PageSettings s, int page, int total)
    {
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float margin = s.Margin, width = s.Width - margin * 2;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        float fontSize = 5.3f;
        while (fontSize > 2.5f)
        {
            using var measureFont = new Font("Malgun Gothic", fontSize, FontStyle.Bold, GraphicsUnit.World);
            if (g.MeasureString(s.Title, measureFont).Width <= width) break;
            fontSize -= .2f;
        }
        using var font = new Font("Malgun Gothic", fontSize, FontStyle.Bold, GraphicsUnit.World);
        g.DrawString(s.Title, font, Brushes.Black, new RectangleF(margin, margin, width, 13), format);
        using var line = new Pen(Color.FromArgb(65,65,65), .22f);
        g.DrawLine(line, margin, margin + 16, s.Width - margin, margin + 16);
        foreach(var item in layout.Items)
        {
            g.DrawImage(item.Receipt.Image,item.Bounds,item.Source,GraphicsUnit.Pixel);
            if (s.Border) { using var border = new Pen(Color.FromArgb(175,181,190), .15f); g.DrawRectangle(border,item.Bounds.X,item.Bounds.Y,item.Bounds.Width,item.Bounds.Height); }
        }
        using var footer = new Font("Malgun Gothic", 2.7f, FontStyle.Regular, GraphicsUnit.World);
        g.DrawString($"{page+1} / {total}", footer, Brushes.Gray, new RectangleF(margin, s.Height-margin-5, width, 5), format);
        var continued=layout.Items.FirstOrDefault(i=>i.Parts>1);
        if(continued is not null)g.DrawString($"긴 영수증 나눔 · {continued.Part}/{continued.Parts}",footer,Brushes.Gray,new RectangleF(margin,s.Height-margin-10,width,5),format);
    }
}

// PDF pages embed the same 300 DPI page image used by PNG/JPEG export.
// This preserves Korean text without relying on a font installed on the viewer's PC.
public static class PdfExport
{
    public static void Write(string path, IReadOnlyList<Receipt> receipts, PageSettings settings)
    {
        var pages=LayoutEngine.Plan(receipts,settings);int count=pages.Count;
        using var stream = File.Create(path);
        var offsets = new List<long> { 0 };
        void Text(string value) { var bytes = Encoding.ASCII.GetBytes(value); stream.Write(bytes); }
        void Object(int id, Action content) { while (offsets.Count <= id) offsets.Add(0); offsets[id] = stream.Position; Text($"{id} 0 obj\n"); content(); Text("\nendobj\n"); }
        Text("%PDF-1.4\n");
        Object(1, () => Text("<< /Type /Catalog /Pages 2 0 R >>"));
        Object(2, () => Text($"<< /Type /Pages /Count {count} /Kids [{string.Join(" ", Enumerable.Range(0,count).Select(i => $"{3+i*3} 0 R"))}] >>"));
        string w = (settings.Width*72/25.4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        string h = (settings.Height*72/25.4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < count; i++)
        {
            using var bitmap = LayoutEngine.Render(pages[i], settings, i, count, 300);
            using var jpeg = new MemoryStream(); SaveJpeg(bitmap, jpeg);
            int id = 3+i*3;
            Object(id, () => Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im {id+1} 0 R >> >> /Contents {id+2} 0 R >>"));
            Object(id+1, () => { Text($"<< /Type /XObject /Subtype /Image /Width {bitmap.Width} /Height {bitmap.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"); jpeg.Position=0; jpeg.CopyTo(stream); Text("\nendstream"); });
            string commands = $"q {w} 0 0 {h} 0 0 cm /Im Do Q\n";
            Object(id+2, () => Text($"<< /Length {Encoding.ASCII.GetByteCount(commands)} >>\nstream\n{commands}endstream"));
        }
        long xref = stream.Position;
        Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Text($"{offset:0000000000} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
    public static void SaveJpeg(Bitmap image, Stream stream)
    {
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
        image.Save(stream, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
    }
}
