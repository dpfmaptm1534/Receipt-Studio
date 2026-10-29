using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;

namespace ReceiptStudio;

public static class ProjectFile
{
    public sealed class Data
    {
        public int Version { get; set; } = 1;
        public PageSettings Settings { get; set; } = new();
        public List<string> Names { get; set; } = [];
        public List<ReceiptInfo>? Information { get; set; }
    }
    public sealed class ReceiptInfo
    {
        public int PrintWidthMm { get; set; }
        public DateTime? PaymentDate { get; set; }
        public bool DateEdited { get; set; }
        public string OcrText { get; set; } = "";
        public string OcrStatus { get; set; } = "미인식";
        public List<DateTime> DateCandidates { get; set; } = [];
    }
    public static void Save(string path, PageSettings settings, IReadOnlyList<Receipt> receipts)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var output = zip.CreateEntry("project.json").Open())
            JsonSerializer.Serialize(output, new Data { Settings=settings, Names=receipts.Select(r=>r.Name).ToList(), Information=receipts.Select(r=>new ReceiptInfo { PrintWidthMm=r.PrintWidthMm,PaymentDate=r.PaymentDate,DateEdited=r.DateEdited,OcrText=r.OcrText,OcrStatus=r.OcrStatus,DateCandidates=r.DateCandidates }).ToList() });
        for (int i=0; i<receipts.Count; i++)
        {
            using var buffer=new MemoryStream(); receipts[i].Image.Save(buffer,ImageFormat.Png);buffer.Position=0;
            using var output=zip.CreateEntry($"images/{i}.png",CompressionLevel.Fastest).Open();buffer.CopyTo(output);
        }
    }
    public static (PageSettings settings, List<Receipt> receipts) Load(string path)
    {
        var imported=new List<Receipt>();
        try
        {
            using var zip=ZipFile.OpenRead(path);
            using var input=(zip.GetEntry("project.json")??throw new InvalidDataException("올바른 작업 파일이 아닙니다.")).Open();
            var data=JsonSerializer.Deserialize<Data>(input)??throw new InvalidDataException("작업 정보가 없습니다.");
            if(data.Version!=1 || data.Settings is null || data.Names is null || data.Settings.PerPage is <1 or >24 || data.Settings.Margin is <5 or >25 || data.Settings.Gap is <0 or >12 || data.Settings.ReceiptWidthMm is <30 or >150 || data.Settings.Title is null || data.Settings.Title.Length>100 || data.Names.Count>1000)
                throw new InvalidDataException("지원하지 않는 작업 설정입니다.");
            for(int i=0;i<data.Names.Count;i++)
            {
                using var inputImage=(zip.GetEntry($"images/{i}.png")??throw new InvalidDataException("이미지가 누락되었습니다.")).Open();
                using var buffer=new MemoryStream();inputImage.CopyTo(buffer);buffer.Position=0;
                using var image=Image.FromStream(buffer);var receipt=new Receipt(data.Names[i],new Bitmap(image));imported.Add(receipt);
                if(data.Information is not null && i<data.Information.Count && data.Information[i] is {} info)
                { if(info.PrintWidthMm!=0 && info.PrintWidthMm is <30 or >150)throw new InvalidDataException("영수증 출력 폭이 잘못되었습니다.");receipt.PrintWidthMm=info.PrintWidthMm;receipt.PaymentDate=info.PaymentDate?.Date;receipt.DateEdited=info.DateEdited;receipt.OcrText=info.OcrText??"";receipt.OcrStatus=info.OcrStatus??"미인식";receipt.DateCandidates=info.DateCandidates??[]; }
            }
            return (data.Settings,imported);
        }
        catch { foreach(var receipt in imported)receipt.Dispose();throw; }
    }
}

