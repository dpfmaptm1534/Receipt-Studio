using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tesseract;

namespace ReceiptStudio;

public sealed record DateReading(DateTime? Date, List<DateTime> Candidates, string Status);

public static class ReceiptDates
{
    static readonly Regex FullDate=new(@"(?<!\d)(?<y>20\d{2})\s*(?:[-./]|년)\s*(?<m>\d{1,2})\s*(?:[-./]|월)\s*(?<d>\d{1,2})(?:\s*일)?(?!\d)",RegexOptions.Compiled);
    static readonly Regex ShortDate=new(@"(?<![\d./-])(?<y>\d{2})\s*[-./]\s*(?<m>\d{1,2})\s*[-./]\s*(?<d>\d{1,2})(?![\d./-])",RegexOptions.Compiled);
    static readonly Regex EndYear=new(@"(?<!\d)(?<a>\d{1,2})\s*[-./]\s*(?<b>\d{1,2})\s*[-./]\s*(?<y>20\d{2})(?!\d)",RegexOptions.Compiled);
    static readonly Regex CompactDate=new(@"(?<!\d)(?<y>20\d{2})(?<m>\d{2})(?<d>\d{2})(?!\d)",RegexOptions.Compiled);
    public static DateReading Parse(string text)
    {
        var candidates=new Dictionary<DateTime,int>();
        string[] lines=text.Replace('\r','\n').Split('\n',StringSplitOptions.RemoveEmptyEntries);
        for(int index=0;index<lines.Length;index++)
        {
            string line=lines[index].Replace('－','-').Replace('／','/').Replace('．','.');
            string context=Regex.Replace(line,@"\s+","");
            string preceding=index>0?Regex.Replace(lines[index-1],@"\s+",""):"";
            int score=Score(context);
            if(score==0 && !Regex.IsMatch(preceding,@"\d"))score=Score(preceding);
            if(score<0)continue;
            foreach(Match match in FullDate.Matches(line))Add(match,0,score);
            foreach(Match match in ShortDate.Matches(line))Add(match,2000,score);
            // Compact dates are used only next to a payment/date label, never from order IDs.
            if(score>=20)foreach(Match match in CompactDate.Matches(line))Add(match,0,score);
            foreach(Match match in EndYear.Matches(line))
            {
                int y=int.Parse(match.Groups["y"].Value),a=int.Parse(match.Groups["a"].Value),b=int.Parse(match.Groups["b"].Value);
                TryAdd(y,a,b,score);TryAdd(y,b,a,score); // Ambiguous day/month stays unresolved.
            }
        }
        var ordered=candidates.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).ToList();
        DateTime? chosen=null;
        if(ordered.Count==1 || ordered.Count>1 && ordered[0].Value==100 && ordered.Skip(1).All(p=>p.Value==20))chosen=ordered[0].Key;
        return new DateReading(chosen,ordered.Select(p=>p.Key).ToList(),chosen.HasValue?"OCR 추정 · 확인 필요":ordered.Count>1?"날짜 여러 개 · 확인 필요":"날짜 미인식");
        void Add(Match m,int yearOffset,int score) => TryAdd(int.Parse(m.Groups["y"].Value)+yearOffset,int.Parse(m.Groups["m"].Value),int.Parse(m.Groups["d"].Value),score);
        void TryAdd(int year,int month,int day,int score)
        {
            if(year<2000 || year>2099 || month<1 || month>12 || day<1 || day>DateTime.DaysInMonth(year,month))return;
            var date=new DateTime(year,month,day);candidates[date]=Math.Max(candidates.GetValueOrDefault(date,-1),score);
        }
    }
    static int Score(string text)
    {
        if(Regex.IsMatch(text,@"주문번호|승인번호|카드번호|사업자|등록번호|전표번호") && !Regex.IsMatch(text,@"일시|일자|결제일|거래일|승인일|date",RegexOptions.IgnoreCase))return -100;
        if(Regex.IsMatch(text,@"유효|만료|생년|제조|유통|적립기한|사용기한|expiry|expires|birth",RegexOptions.IgnoreCase))return -100;
        if(Regex.IsMatch(text,@"결제|거래|승인|매출|payment|transaction",RegexOptions.IgnoreCase))return 100;
        if(Regex.IsMatch(text,@"일시|일자|날짜|구매|이용일|판매일|date",RegexOptions.IgnoreCase))return 60;
        if(Regex.IsMatch(text,@"주문|발행|출력|order",RegexOptions.IgnoreCase))return 20;
        return 0;
    }
    public static List<Receipt> Sort(IEnumerable<Receipt> receipts,bool descending)
    {
        var dated=receipts.Where(r=>r.PaymentDate.HasValue);
        return (descending?dated.OrderByDescending(r=>r.PaymentDate):dated.OrderBy(r=>r.PaymentDate)).Concat(receipts.Where(r=>!r.PaymentDate.HasValue)).ToList();
    }
}

public sealed class ReceiptOcr : IDisposable
{
    readonly TesseractEngine engine;
    public ReceiptOcr(string? cacheDirectory=null)
    {
        string root=ExtractResources(cacheDirectory);
        TesseractEnviornment.CustomSearchPath=root;
        engine=new TesseractEngine(Path.Combine(root,"tessdata"),"kor+eng",EngineMode.LstmOnly);
        engine.SetVariable("user_defined_dpi","300");
    }
    static string ExtractResources(string? cacheDirectory)
    {
        string root=cacheDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReceiptStudio","ocr-5.2.0");
        var assembly=Assembly.GetExecutingAssembly();
        foreach(string resource in assembly.GetManifestResourceNames().Where(n=>n.StartsWith("ocr.")))
        {
            string name=resource[4..];string directory=Path.Combine(root,name.EndsWith(".dll")?"x64":"tessdata");Directory.CreateDirectory(directory);
            using var input=assembly.GetManifestResourceStream(resource)!;
            string path=Path.Combine(directory,name);bool valid=false;
            if(File.Exists(path))
            {
                using var existing=File.OpenRead(path);
                valid=existing.Length==input.Length && SHA256.HashData(existing).SequenceEqual(SHA256.HashData(input));input.Position=0;
            }
            if(valid)continue;
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try { using(var output=File.Create(temp))input.CopyTo(output);File.Move(temp,path,true); }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }
        return root;
    }
    public (string text,DateReading reading) Recognize(Bitmap image)
    {
        // Limit large camera images and enlarge small receipts without distorting their ratio.
        double scale=Math.Min(2,Math.Max(1,1400.0/image.Width));scale=Math.Min(scale,4200.0/Math.Max(image.Width,image.Height));
        using var normalized=new Bitmap(Math.Max(1,(int)(image.Width*scale)),Math.Max(1,(int)(image.Height*scale)),PixelFormat.Format24bppRgb);
        using(var graphics=Graphics.FromImage(normalized))
        { graphics.Clear(Color.White);graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;graphics.DrawImage(image,0,0,normalized.Width,normalized.Height); }
        using var buffer=new MemoryStream();normalized.Save(buffer,System.Drawing.Imaging.ImageFormat.Png);
        using var pix=Pix.LoadFromMemory(buffer.ToArray());
        string text;
        using(var page=engine.Process(pix,PageSegMode.Auto))text=page.GetText();
        var reading=ReceiptDates.Parse(text);
        if(reading.Candidates.Count==0)
        {
            using var page=engine.Process(pix,PageSegMode.SparseText);string retry=page.GetText();
            if(!string.IsNullOrWhiteSpace(retry)){text+="\n\n[보완 인식]\n"+retry;reading=ReceiptDates.Parse(text);}
        }
        return (text,reading);
    }
    public void Dispose()=>engine.Dispose();
}
