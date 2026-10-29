using System.Drawing.Imaging;

namespace ReceiptStudio;

internal static class LayoutSample
{
    public static void Run(string path)
    {
        using var source=Receipt.Read(path);var receipts=new List<Receipt>();
        var regions=new[]{new Rectangle(25,5,402,939),new Rectangle(444,5,403,895),new Rectangle(864,5,438,736),new Rectangle(41,985,386,661),new Rectangle(457,985,390,867)};
        try
        {
            foreach(var region in regions)
            {
                float sx=source.Width/1334f,sy=source.Height/1888f;
                var crop=new Rectangle((int)(region.X*sx),(int)(region.Y*sy),(int)(region.Width*sx),(int)(region.Height*sy));
                receipts.Add(new Receipt($"샘플 {receipts.Count+1}",source.Clone(crop,PixelFormat.Format24bppRgb)));
            }
            var settings=new PageSettings{Landscape=true,PerPage=6};var pages=LayoutEngine.Plan(receipts,settings);
            string folder=Path.Combine(AppContext.BaseDirectory,"test-output");Directory.CreateDirectory(folder);
            for(int i=0;i<pages.Count;i++){using var bitmap=LayoutEngine.Render(pages[i],settings,i,pages.Count,130);bitmap.Save(Path.Combine(folder,$"readable-layout-{i+1}.png"));}
            using(var control=new PreviewCanvas { Size=new Size(1100,800) })
            {
                control.SetPage(LayoutEngine.Render(pages[0],settings,0,pages.Count,130),false,pages[0],settings.Width,settings.Height,receipts[0]);
                using var preview=new Bitmap(control.Width,control.Height);control.DrawToBitmap(preview,control.ClientRectangle);preview.Save(Path.Combine(folder,"mouse-resize-preview.png"));
            }
            PdfExport.Write(Path.Combine(folder,"readable-layout.pdf"),receipts,settings);
            string project=Path.Combine(folder,"readable-layout.receipt");if(File.Exists(project))File.Delete(project);ProjectFile.Save(project,settings,receipts);
            File.WriteAllText(Path.Combine(folder,"readable-layout.txt"),$"{receipts.Count} receipts, {pages.Count} pages, widths: "+string.Join(", ",pages.SelectMany(p=>p.Items).Select(i=>i.Bounds.Width)));
        }
        finally{foreach(var r in receipts)r.Dispose();}
    }
}
