using System.Drawing.Imaging;
using System.Text.Json;

namespace ReceiptStudio;

internal static class SelfTest
{
    public static void Run()
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"test-output");Directory.CreateDirectory(folder);
        var receipts=new List<Receipt>();int checks=0;
        void Assert(bool value,string message) { checks++;if(!value)throw new Exception(message); }
        try
        {
            checks+=InteractionTests.Run();
            var random=new Random(42);
            for(int i=0;i<24;i++)
            {
                var image=new Bitmap(random.Next(180,650),random.Next(250,1600));
                using var g=Graphics.FromImage(image);g.Clear(Color.White);
                using var f=new Font("Malgun Gothic",20,FontStyle.Bold);
                g.DrawString($"샘플 영수증 {i+1}\n\n2026.10.29\n\n합계 11,000원",f,Brushes.Black,10,12);
                receipts.Add(new Receipt($"테스트 {i+1}",image));
            }
            foreach(bool landscape in new[]{false,true}) foreach(int count in new[]{1,2,3,4,6,9,12,24}) foreach(int margin in new[]{5,25}) foreach(int gap in new[]{0,12})
            {
                var settings=new PageSettings { Landscape=landscape,PerPage=count,Margin=margin,Gap=gap };
                var bounds=new RectangleF(margin,margin+21,settings.Width-margin*2,settings.Height-margin*2-30);
                var chosen=receipts.Take(count).ToList();var pages=LayoutEngine.Plan(chosen,settings);
                Assert(pages.SelectMany(p=>p.Items).Select(p=>p.Receipt).Distinct().SequenceEqual(chosen),"Count or reading order mismatch");
                foreach(var layout in pages)
                {
                    Assert(layout.Items.Count<=count,"Maximum count exceeded");
                    for(int i=0;i<layout.Items.Count;i++)
                    {
                    var item=layout.Items[i];var box=item.Bounds;Assert(box.Width>0 && box.Height>0,"Nonpositive rectangle");
                    Assert(box.X>=bounds.X-.01 && box.Y>=bounds.Y-.01 && box.Right<=bounds.Right+.01 && box.Bottom<=bounds.Bottom+.01,"Outside page");
                    Assert(Math.Abs(box.Width-60)<.001,"Readability width was reduced");
                    Assert(Math.Abs(box.Width/box.Height-(float)item.Source.Width/item.Source.Height)<.001,"Aspect ratio changed");
                    for(int j=i+1;j<layout.Items.Count;j++){var intersect=RectangleF.Intersect(box,layout.Items[j].Bounds);Assert(intersect.Width<.01||intersect.Height<.01,"Overlap");}
                    }
                }
                foreach(var receipt in chosen)
                {
                    int bottom=0;foreach(var item in pages.SelectMany(p=>p.Items).Where(p=>ReferenceEquals(p.Receipt,receipt)))
                    { Assert(item.Source.Top==bottom,"Source pixels skipped or repeated");bottom=item.Source.Bottom; }
                    Assert(bottom==receipt.Image.Height,"Receipt content lost");
                }
            }
            foreach(bool landscape in new[]{false,true})
            {
                var settings=new PageSettings { Landscape=landscape };
                var pages=LayoutEngine.Plan(receipts.Take(7).ToList(),settings);
                using var image=LayoutEngine.Render(pages[0],settings,0,pages.Count,300);
                string name=landscape?"landscape":"portrait";image.Save(Path.Combine(folder,name+".png"),ImageFormat.Png);
                using(var output=File.Create(Path.Combine(folder,name+".jpg")))PdfExport.SaveJpeg(image,output);
                PdfExport.Write(Path.Combine(folder,name+".pdf"),receipts.Take(7).ToList(),settings);
                Assert(image.Width==(int)Math.Round(settings.Width*300/25.4f),"Export width");
            }
            var roundtrip=JsonSerializer.Deserialize<PageSettings>(JsonSerializer.Serialize(new PageSettings()));Assert(roundtrip?.Title==new PageSettings().Title,"Korean title roundtrip");
            receipts[0].PrintWidthMm=90;
            var custom=LayoutEngine.Plan(receipts,new PageSettings());
            Assert(custom.SelectMany(p=>p.Items).Where(p=>ReferenceEquals(p.Receipt,receipts[0])).All(i=>Math.Abs(i.Bounds.Width-90)<.001),"Individual print width");
            using(var shortReceipt=new Receipt("short",new Bitmap(400,400)))
            using(var longReceipt=new Receipt("long",new Bitmap(400,900)))
            {
                var page=LayoutEngine.Plan([shortReceipt,longReceipt],new PageSettings())[0];
                Assert(page.Items.Count==2 && page.Items[0].Bounds.Width==page.Items[1].Bounds.Width && page.Items[0].Bounds.Height<page.Items[1].Bounds.Height,"Different heights must be preserved");
                Assert(page.Items[0].Bounds.Y==page.Items[1].Bounds.Y,"Top alignment");
            }
            string project=Path.Combine(folder,"roundtrip.receipt");
            if(File.Exists(project))File.Delete(project);
            ProjectFile.Save(project,new PageSettings { Landscape=true,PerPage=4,Gap=7,ReceiptWidthMm=70,Title="2026년 10월 지출영수증 이재원 사원" },receipts.Take(7).ToList());
            var loaded=ProjectFile.Load(project);
            try
            {
                Assert(loaded.receipts.Count==7 && loaded.settings.Landscape && loaded.settings.PerPage==4 && loaded.settings.Gap==7,"Project settings roundtrip");
                Assert(loaded.settings.ReceiptWidthMm==70 && loaded.receipts[0].PrintWidthMm==90,"Width persistence");
                for(int i=0;i<7;i++)Assert(loaded.receipts[i].Name==receipts[i].Name && loaded.receipts[i].Image.Size==receipts[i].Image.Size && loaded.receipts[i].Image.GetPixel(10,10)==receipts[i].Image.GetPixel(10,10),"Project images roundtrip");
            }
            finally { foreach(var receipt in loaded.receipts)receipt.Dispose(); }
            File.WriteAllText(Path.Combine(folder,"result.txt"),$"PASS: {checks} checks. Physical widths preserved, mixed heights, overflow pagination, lossless long-receipt continuation, original aspect ratios, project persistence; portrait/landscape PNG, JPEG, PDF generated.");
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(folder,"result.txt"),"FAIL: "+ex);Environment.ExitCode=1; }
        finally { foreach(var r in receipts)r.Dispose(); }
    }
}
