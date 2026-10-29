using System.Text;

namespace ReceiptStudio;

internal static class OcrTests
{
    public static void Run(string? sample)
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"test-output");Directory.CreateDirectory(folder);var log=new StringBuilder();int checks=0;
        void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
        try
        {
            foreach(var input in new[]{"결제일시 2026/09/02 09:54:31","거래일시 2026-09-02","2026.9.2","2026년 9월 2일","승인일 26/09/02","결제 일시 20260902","결제일시\n2026/09/02"})
                Assert(ReceiptDates.Parse(input).Date==new DateTime(2026,9,2),"Expected September 2: "+input);
            Assert(ReceiptDates.Parse("유효기간 2027/01/01\n거래일시 2026/10/28").Date==new DateTime(2026,10,28),"Ignore expiry");
            Assert(ReceiptDates.Parse("주문일 2026/10/27\n결제일시 2026/10/28").Date==new DateTime(2026,10,28),"Prefer payment");
            Assert(ReceiptDates.Parse("2026/09/02\n2026/09/08").Date is null,"Multiple receipts must be ambiguous");
            Assert(ReceiptDates.Parse("결제일 2026/09/02\n결제일 2026/09/08").Date is null,"Multiple payment dates must be ambiguous");
            Assert(ReceiptDates.Parse("11/09/2026").Date is null,"Ambiguous day/month");
            Assert(ReceiptDates.Parse("23/09/2026").Date==new DateTime(2026,9,23),"Unambiguous day/month");
            foreach(string input in new[]{"2026/02/30","2026/13/01","주문번호 202609221000001","주문번호 20260922","카드번호 557042******","2026/09","","유효기간 2026/10/28"})Assert(ReceiptDates.Parse(input).Date is null,"False date: "+input);
            using var a=new Receipt("A",new Bitmap(20,20)){PaymentDate=new DateTime(2026,10,2)};
            using var b=new Receipt("B",new Bitmap(20,20)){PaymentDate=new DateTime(2026,10,1)};
            using var c=new Receipt("C",new Bitmap(20,20));using var d=new Receipt("D",new Bitmap(20,20)){PaymentDate=a.PaymentDate};
            Assert(string.Join("",ReceiptDates.Sort(new[]{a,c,b,d},false).Select(r=>r.Name))=="BADC","Stable ascending, nulls last");
            Assert(string.Join("",ReceiptDates.Sort(new[]{a,c,b,d},true).Select(r=>r.Name))=="ADBC","Stable descending, nulls last");
            a.DateEdited=true;a.OcrText="결제일 2026/10/02";a.DateCandidates=[a.PaymentDate.Value];a.OcrStatus="OCR 추정";
            string path=Path.Combine(folder,"ocr-roundtrip-"+Guid.NewGuid().ToString("N")+".receipt");ProjectFile.Save(path,new PageSettings(),[a,b,c]);
            var loaded=ProjectFile.Load(path);try{Assert(loaded.receipts[0].PaymentDate==a.PaymentDate && loaded.receipts[0].DateEdited && loaded.receipts[0].OcrText==a.OcrText && loaded.receipts[0].DateCandidates.SequenceEqual(a.DateCandidates),"OCR project roundtrip");}finally{foreach(var r in loaded.receipts)r.Dispose();}
            File.Delete(path);
            // Version-1 projects without metadata must still open.
            path=Path.Combine(folder,"legacy-"+Guid.NewGuid().ToString("N")+".receipt");
            ProjectFile.Save(path,new PageSettings(),[a]);
            using(var zip=System.IO.Compression.ZipFile.Open(path,System.IO.Compression.ZipArchiveMode.Update))
            { zip.GetEntry("project.json")!.Delete();using var output=zip.CreateEntry("project.json").Open();System.Text.Json.JsonSerializer.Serialize(output,new{Version=1,Settings=new PageSettings(),Names=new[]{"A"}}); }
            var legacy=ProjectFile.Load(path);try{Assert(legacy.receipts[0].PaymentDate is null && legacy.receipts[0].OcrText=="","Legacy project");}finally{foreach(var r in legacy.receipts)r.Dispose();}File.Delete(path);
            a.ClearRecognition();Assert(a.PaymentDate is null && !a.DateEdited && a.OcrText=="","Crop invalidation");
            using var ocr=new ReceiptOcr(Path.Combine(folder,"ocr-cache"));
            using var synthetic=new Bitmap(1200,500);
            using(var g=Graphics.FromImage(synthetic)){g.Clear(Color.White);using var font=new Font("Malgun Gothic",32);g.DrawString("카드 영수증\n결제일시 2026/10/28 13:45:00\n합계 11,000원",font,Brushes.Black,40,40);}
            var result=ocr.Recognize(synthetic);log.AppendLine("Synthetic OCR date: "+result.reading.Date?.ToString("yyyy-MM-dd"));
            Assert(result.reading.Date==new DateTime(2026,10,28),"Real OCR synthetic Korean receipt: "+result.text);
            if(sample is not null)
            {
                using var image=Receipt.Read(sample);var read=ocr.Recognize(image);
                log.AppendLine("Attached collage candidates: "+string.Join(", ",read.reading.Candidates.Select(t=>t.ToString("yyyy-MM-dd"))));
                log.AppendLine("Collage chosen: "+(read.reading.Date?.ToString("yyyy-MM-dd")??"none (review required)"));
                Assert(read.reading.Candidates.Count>=2 && read.reading.Date is null,"Multi-receipt image requires review");
            }
            log.AppendLine($"PASS: {checks} OCR/date/sort/persistence checks");
        }
        catch(Exception ex){log.AppendLine("FAIL: "+ex);Environment.ExitCode=1;}
        File.WriteAllText(Path.Combine(folder,"ocr-result.txt"),log.ToString());
    }
}
