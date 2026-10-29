using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO.Compression;
using System.Text.Json;

namespace ReceiptStudio;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { SelfTest.Run(); return; }
        if (args.Length==2 && args[0]=="--layout-sample") { LayoutSample.Run(args[1]);return; }
        if (args.Contains("--ocr-test")) { OcrTests.Run(args.SkipWhile(a=>a!="--ocr-test").Skip(1).FirstOrDefault());return; }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "작업을 완료하지 못했습니다", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(args));
    }
}

public sealed partial class MainForm : Form
{
    readonly List<Receipt> receipts = [];
    PageSettings settings = new();
    readonly TextBox title = new();
    readonly ComboBox orientation = new();
    readonly NumericUpDown perPage = new(), margin = new(), gap = new();
    readonly NumericUpDown defaultWidth = new(), selectedWidth = new();
    readonly CheckBox useDefaultWidth = new();
    bool updatingWidth;
    bool selectingFromPreview;
    List<ReceiptPage> pagePlan=[new ReceiptPage()];
    readonly CheckBox border = new();
    readonly ListBox list = new();
    readonly PreviewCanvas preview = new();
    readonly Label pageLabel = new(), status = new();
    readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 180 };
    readonly Button previous, next;
    int page;
    bool dirty, loading;
    string? projectPath;
    int PageCount => pagePlan.Count;
    public MainForm(string[] args)
    {
        Text = "영수증 정리 · Receipt Studio OCR";
        Font = new Font("Malgun Gothic", 10);
        BackColor = Color.FromArgb(245,247,251);
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1380, 930); MinimumSize = new Size(1050, 740);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.White, Padding = new Padding(22,12,22,8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,630));
        var brand = new Label { Text = "영수증 정리\n이미지를 넣고, 배치하고, 바로 출력하세요", Dock=DockStyle.Fill, Font=new Font(Font.FontFamily,12,FontStyle.Bold), ForeColor=Color.FromArgb(29,45,67) };
        header.Controls.Add(brand,0,0);
        var toolbar = new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, WrapContents=false };
        toolbar.Controls.Add(Button("인쇄", (_,_) => Print(), true, 92));
        toolbar.Controls.Add(Button("파일로 저장", (_,_) => Export(), true, 128));
        toolbar.Controls.Add(Button("작업 저장", (_,_) => SaveProject(), false, 105));
        toolbar.Controls.Add(BlockDuringOcr(Button("작업 열기", (_,_) => OpenProject(), false, 105)));
        toolbar.Controls.Add(BlockDuringOcr(Button("새 작업", (_,_) => NewProject(), false, 90)));
        header.Controls.Add(toolbar,1,0); root.Controls.Add(header,0,0);
        var body = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, Padding=new Padding(14), BackColor=BackColor };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,340)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.Controls.Add(body,0,1);
        var sidebar = new Panel { Dock=DockStyle.Fill, BackColor=Color.White, AutoScroll=true, Padding=new Padding(16), Margin=new Padding(0,0,14,0) };
        body.Controls.Add(sidebar,0,0);
        var side = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=1, Padding=new Padding(0), Width=290 };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); sidebar.Controls.Add(side);
        void Add(Control control, int height) { int r=side.RowCount++; side.RowStyles.Add(new RowStyle(SizeType.Absolute,height)); control.Dock=DockStyle.Fill; side.Controls.Add(control,0,r); }
        Add(Section("01  용지와 제목"),32);
        Add(Label("상단 제목 · 직접 수정할 수 있어요"),27);
        title.Text=settings.Title; title.MaxLength=100; title.Multiline=true; title.Margin=new Padding(0,0,0,7); Add(title,62);
        Add(Label("A4 용지 방향"),26);
        orientation.DropDownStyle=ComboBoxStyle.DropDownList; orientation.Items.AddRange(["세로  ·  210 × 297 mm","가로  ·  297 × 210 mm"]); orientation.SelectedIndex=0; Add(orientation,38);
        Add(Label("한 페이지 최대 장수 (공간 부족 시 다음 장)"),27);
        perPage.Minimum=1; perPage.Maximum=24; perPage.Value=6; Add(perPage,36);
        Add(Label("기본 출력 폭 (mm) · 높이는 비율대로 유지"),26);
        defaultWidth.Minimum=30;defaultWidth.Maximum=150;defaultWidth.Value=60;Add(defaultWidth,36);
        var spacing = new TableLayoutPanel { ColumnCount=2, Dock=DockStyle.Fill, Margin=new Padding(0) };
        spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50)); spacing.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        margin.Minimum=5; margin.Maximum=25; margin.Value=10;
        gap.Minimum=0; gap.Maximum=12; gap.Value=4;
        spacing.Controls.Add(Label("여백 (mm)"),0,0); spacing.Controls.Add(Label("영수증 간격 (mm)"),1,0);
        spacing.Controls.Add(margin,0,1); spacing.Controls.Add(gap,1,1); margin.Dock=gap.Dock=DockStyle.Fill; Add(spacing,66);
        border.Text="영수증 테두리 표시"; border.Checked=true; Add(border,33);
        Add(Section("02  영수증 이미지"),38);
        Add(BlockDuringOcr(Button("+  이미지 추가", (_,_) => AddImages(), true)),42);
        Add(Label("여러 장 선택 또는 이 창에 파일 끌어놓기"),27);
        list.DrawMode=DrawMode.OwnerDrawFixed; list.ItemHeight=58; list.IntegralHeight=false; list.BorderStyle=BorderStyle.FixedSingle;
        list.DrawItem += DrawReceipt; list.SelectedIndexChanged += (_,_) => { UpdateWidthControls();if(selectingFromPreview)preview.SelectReceipt(list.SelectedItem as Receipt);else if(list.SelectedItem is Receipt selected)RefreshPreview(selected);else preview.SelectReceipt(null); };
        preview.SelectionChanged+=selected=> { selectingFromPreview=true;try{list.SelectedItem=selected;}finally{selectingFromPreview=false;}UpdateWidthControls(); };
        preview.WidthCommitted+=(receipt,width)=> { if(!receipts.Contains(receipt))return;receipt.PrintWidthMm=width;dirty=true;UpdateWidthControls();RefreshPreview(receipt); };
        Add(list,185);
        var sizePanel=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0),Padding=new Padding(12,4,0,0),BackColor=Color.White };
        var sizeLabel=Label("선택 영수증 폭 (mm)");sizeLabel.Size=new Size(160,30);sizePanel.Controls.Add(sizeLabel);
        selectedWidth.Minimum=30;selectedWidth.Maximum=150;selectedWidth.Value=60;selectedWidth.Width=80;selectedWidth.Enabled=false;sizePanel.Controls.Add(selectedWidth);
        useDefaultWidth.Text="기본 폭 사용";useDefaultWidth.Width=120;useDefaultWidth.Checked=true;useDefaultWidth.Enabled=false;sizePanel.Controls.Add(useDefaultWidth);
        var sizeHint=Label("미리보기 영수증 클릭 → 파란 모서리 드래그");sizeHint.Size=new Size(310,30);sizePanel.Controls.Add(sizeHint);
        selectedWidth.ValueChanged+=(_,_)=>SetSelectedWidth();useDefaultWidth.CheckedChanged+=(_,_)=>SetSelectedWidth();
        var edit = new FlowLayoutPanel { WrapContents=true, Margin=new Padding(0), Padding=new Padding(0) };
        edit.Controls.Add(Button("↶ 회전", (_,_) => Rotate(), false,86));
        edit.Controls.Add(Button("자르기", (_,_) => Crop(), false,86));
        edit.Controls.Add(Button("삭제", (_,_) => Remove(), false,86));
        edit.Controls.Add(Button("↑ 위로", (_,_) => MoveReceipt(-1), false,86));
        edit.Controls.Add(Button("↓ 아래로", (_,_) => MoveReceipt(1), false,86));
        edit.Controls.Add(Button("복제", (_,_) => Duplicate(), false,86));foreach(Button b in edit.Controls)BlockDuringOcr(b); Add(edit,90);
        Add(Label("원본 비율을 유지해 자동으로 배치합니다.\n합쳐진 이미지는 복제 후 각각 자르세요."),45);
        var previewArea = new TableLayoutPanel { Dock=DockStyle.Fill, RowCount=4, ColumnCount=1, Margin=new Padding(0) };
        previewArea.RowStyles.Add(new RowStyle(SizeType.Absolute,47)); previewArea.RowStyles.Add(new RowStyle(SizeType.Absolute,88));previewArea.RowStyles.Add(new RowStyle(SizeType.Absolute,42));previewArea.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var navigation = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false, BackColor=Color.White, Padding=new Padding(10,0,0,0) };
        navigation.Controls.Add(new Label { Text="인쇄 미리보기",AutoSize=false,Width=145,Height=38,TextAlign=ContentAlignment.MiddleLeft,Font=new Font(Font,FontStyle.Bold) });
        previous=Button("‹",(_,_) => { page--; RefreshPreview(); },false,40); next=Button("›",(_,_) => { page++; RefreshPreview(); },false,40);
        navigation.Controls.Add(previous); pageLabel.Size=new Size(110,38); pageLabel.TextAlign=ContentAlignment.MiddleCenter; navigation.Controls.Add(pageLabel); navigation.Controls.Add(next);
        navigation.Controls.Add(new Label { Text="항상 표시 · 변경 즉시 반영",AutoSize=true,Margin=new Padding(18,11,0,0),ForeColor=Color.FromArgb(103,115,133) });
        previewArea.Controls.Add(navigation,0,0);previewArea.Controls.Add(BuildOcrToolbar(),0,1);previewArea.Controls.Add(sizePanel,0,2); preview.Dock=DockStyle.Fill; previewArea.Controls.Add(preview,0,3); body.Controls.Add(previewArea,1,0);
        status.Dock=DockStyle.Fill; status.Padding=new Padding(24,0,0,0); status.TextAlign=ContentAlignment.MiddleLeft; status.ForeColor=Color.FromArgb(85,99,117); root.Controls.Add(status,0,2);
        title.TextChanged += Changed; orientation.SelectedIndexChanged += Changed; perPage.ValueChanged += Changed; margin.ValueChanged += Changed; gap.ValueChanged += Changed; border.CheckedChanged += Changed;
        defaultWidth.ValueChanged+=Changed;
        refreshTimer.Tick += (_,_) => { refreshTimer.Stop(); RefreshPreview(); };
        AllowDrop=true;
        DragEnter += (_,e) => e.Effect=!ocrBusy && e.Data?.GetDataPresent(DataFormats.FileDrop)==true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_,e) => { if(e.Data?.GetData(DataFormats.FileDrop) is string[] paths) Import(paths); };
        FormClosing += (_,e) => { if(!ConfirmUnsaved()) e.Cancel=true; };
        Shown += (_,_) => { if(args.Length>0) Import(args.Where(File.Exists)); RefreshPreview(); };
        RefreshPreview();
    }
    Label Label(string text) => new() { Text=text, ForeColor=Color.FromArgb(100,112,130), Font=new Font(Font.FontFamily,9), TextAlign=ContentAlignment.MiddleLeft, AutoSize=false, Margin=new Padding(0) };
    Label Section(string text) => new() { Text=text,Font=new Font(Font,FontStyle.Bold),ForeColor=Color.FromArgb(32,49,72),TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0) };
    Button Button(string text, EventHandler action, bool primary=false, int width=100)
    {
        var button=new Button { Text=text,Width=width,Height=37,FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(38,99,235):Color.White,ForeColor=primary?Color.White:Color.FromArgb(46,61,82),Cursor=Cursors.Hand,Margin=new Padding(3),UseVisualStyleBackColor=false };
        button.FlatAppearance.BorderColor=primary?button.BackColor:Color.FromArgb(219,225,234); button.Click+=action; return button;
    }
    void Changed(object? sender, EventArgs e)
    {
        if(loading) return;
        settings.Title=title.Text.Replace("\r", "").Replace("\n", " "); settings.Landscape=orientation.SelectedIndex==1;
        settings.PerPage=(int)perPage.Value; settings.Margin=(int)margin.Value; settings.Gap=(int)gap.Value; settings.Border=border.Checked;
        settings.ReceiptWidthMm=(int)defaultWidth.Value;UpdateWidthControls();
        dirty=true; refreshTimer.Stop(); refreshTimer.Start();
    }
    void RefreshPreview(Receipt? reveal=null)
    {
        pagePlan=ReceiptStudio.LayoutEngine.Plan(receipts,settings);
        if(reveal is not null)page=pagePlan.FindIndex(p=>p.Items.Any(i=>ReferenceEquals(i.Receipt,reveal)));
        page=Math.Clamp(page,0,PageCount-1);
        var image=ReceiptStudio.LayoutEngine.Render(pagePlan[page],settings,page,PageCount,110);
        preview.SetPage(image,receipts.Count==0,pagePlan[page],settings.Width,settings.Height,list.SelectedItem as Receipt);
        pageLabel.Text=$"{page+1} / {PageCount} 페이지"; previous.Enabled=page>0; next.Enabled=page<PageCount-1;
        status.Text=$"영수증 {receipts.Count}장   ·   기본 폭 {settings.ReceiptWidthMm}mm / 높이 비율 유지   ·   최대 {settings.PerPage}장씩   ·   실제 {PageCount}페이지   ·   300 DPI";
        Text=$"{(dirty?"● ":"")}영수증 정리 · Receipt Studio OCR";
    }
    void ReloadList(int selected=-1) { list.BeginUpdate(); list.Items.Clear(); foreach(var r in receipts) list.Items.Add(r); if(selected>=0 && selected<receipts.Count) list.SelectedIndex=selected; list.EndUpdate();UpdateWidthControls(); dirty=true; RefreshPreview(); }
    void UpdateWidthControls()
    {
        updatingWidth=true;var r=list.SelectedItem as Receipt;
        useDefaultWidth.Enabled=r is not null;useDefaultWidth.Checked=r is null || r.PrintWidthMm==0;
        selectedWidth.Enabled=r is not null && !useDefaultWidth.Checked;
        selectedWidth.Value=r is null || r.PrintWidthMm==0?settings.ReceiptWidthMm:r.PrintWidthMm;updatingWidth=false;
    }
    void SetSelectedWidth()
    {
        if(updatingWidth || list.SelectedItem is not Receipt r)return;
        r.PrintWidthMm=useDefaultWidth.Checked?0:(int)selectedWidth.Value;UpdateWidthControls();dirty=true;RefreshPreview(r);
    }
    void DrawReceipt(object? sender, DrawItemEventArgs e)
    {
        if(e.Index<0 || e.Index>=receipts.Count) return; e.DrawBackground(); var r=receipts[e.Index];
        var thumb=PreviewCanvas.Fit(r.Image.Size,new RectangleF(e.Bounds.X+7,e.Bounds.Y+5,40,48)); e.Graphics.DrawImage(r.Image,thumb);
        using var brush=new SolidBrush(e.ForeColor);
        var rect=new RectangleF(e.Bounds.X+57,e.Bounds.Y+8,e.Bounds.Width-65,23);
        using var format=new StringFormat { Trimming=StringTrimming.EllipsisCharacter, FormatFlags=StringFormatFlags.NoWrap };
        e.Graphics.DrawString($"{e.Index+1:00}  {r.Name}",Font,brush,rect,format);
        using var small=new Font(Font.FontFamily,8);
        e.Graphics.DrawString(r.DateLabel,small,brush,e.Bounds.X+57,e.Bounds.Y+33); e.DrawFocusRectangle();
    }
    void AddImages() { using var dialog=new OpenFileDialog { Filter="이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif|모든 파일|*.*",Multiselect=true,Title="영수증 이미지 선택" }; if(dialog.ShowDialog(this)==DialogResult.OK) Import(dialog.FileNames); }
    void Import(IEnumerable<string> paths)
    {
        if(ocrBusy)return;
        var errors=new List<string>(); int before=receipts.Count;
        foreach(var path in paths) { try { receipts.Add(new Receipt(Path.GetFileName(path),Receipt.Read(path))); } catch(Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); } }
        if(receipts.Count>before) ReloadList(before);
        if(errors.Count>0) MessageBox.Show(this,string.Join("\n",errors),"일부 이미지를 열 수 없습니다");
    }
    void Rotate() { if(ocrBusy)return;if(list.SelectedIndex is int i && i>=0) { receipts[i].Image.RotateFlip(RotateFlipType.Rotate270FlipNone); ReloadList(i); } }
    void Remove() { if(ocrBusy)return;int i=list.SelectedIndex; if(i<0)return; receipts[i].Dispose(); receipts.RemoveAt(i); ReloadList(Math.Min(i,receipts.Count-1)); }
    void MoveReceipt(int delta) { if(ocrBusy)return;int i=list.SelectedIndex,j=i+delta; if(i<0||j<0||j>=receipts.Count)return; (receipts[i],receipts[j])=(receipts[j],receipts[i]); ReloadList(j); }
    void Duplicate() { if(ocrBusy)return;int i=list.SelectedIndex;if(i<0)return;var r=receipts[i];receipts.Insert(i+1,new Receipt(r.Name+" (복사)",new Bitmap(r.Image)) { PrintWidthMm=r.PrintWidthMm,PaymentDate=r.PaymentDate,DateEdited=r.DateEdited,OcrText=r.OcrText,OcrStatus=r.OcrStatus,DateCandidates=[..r.DateCandidates] });ReloadList(i+1); }
    void Crop()
    {
        if(ocrBusy)return;
        int i=list.SelectedIndex;if(i<0)return;
        using var dialog=new CropDialog(receipts[i].Image);
        if(dialog.ShowDialog(this)!=DialogResult.OK || dialog.Cropped is null)return;
        receipts[i].Image.Dispose(); receipts[i].Image=dialog.Cropped;receipts[i].ClearRecognition(); ReloadList(i);
    }
    bool ConfirmUnsaved()
    {
        if(ocrBusy) { MessageBox.Show(this,"OCR 처리 중입니다. ‘중지’를 누른 뒤 현재 이미지 처리가 끝나면 다시 시도해 주세요.","OCR 처리 중");return false; }
        if(!dirty)return true;
        var result=MessageBox.Show(this,"변경한 작업을 저장할까요?\n작업 파일에는 제목, 배치 설정, 영수증 이미지가 함께 보관됩니다.","작업 저장",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
        return result==DialogResult.No || result==DialogResult.Yes && SaveProject();
    }
    void ClearReceipts() { foreach(var r in receipts)r.Dispose();receipts.Clear(); }
    void NewProject()
    {
        if(!ConfirmUnsaved())return; ClearReceipts();settings=new PageSettings();projectPath=null;page=0;ApplySettings();ReloadList();dirty=false;RefreshPreview();
    }
    void ApplySettings()
    {
        loading=true; title.Text=settings.Title; orientation.SelectedIndex=settings.Landscape?1:0; perPage.Value=settings.PerPage; margin.Value=settings.Margin; gap.Value=settings.Gap;border.Checked=settings.Border;defaultWidth.Value=settings.ReceiptWidthMm;loading=false;
    }
    bool SaveProject()
    {
        using var dialog=new SaveFileDialog { Filter="영수증 작업 파일|*.receipt",FileName=projectPath is null?SafeName(settings.Title)+".receipt":Path.GetFileName(projectPath),InitialDirectory=projectPath is null?"":Path.GetDirectoryName(projectPath),Title="수정 가능한 작업 저장" };
        if(dialog.ShowDialog(this)!=DialogResult.OK)return false;
        string temp=dialog.FileName+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            ProjectFile.Save(temp,settings,receipts);
            File.Move(temp,dialog.FileName,true);projectPath=dialog.FileName;dirty=false;RefreshPreview();return true;
        }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"저장 실패");return false; }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    void OpenProject()
    {
        if(!ConfirmUnsaved())return;
        using var dialog=new OpenFileDialog { Filter="영수증 작업 파일|*.receipt" };if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        var imported=new List<Receipt>();
        try
        {
            var loaded=ProjectFile.Load(dialog.FileName);imported=loaded.receipts;
            ClearReceipts();receipts.AddRange(imported);imported.Clear();settings=loaded.settings;projectPath=dialog.FileName;page=0;ApplySettings();ReloadList();dirty=false;RefreshPreview();
        }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"열기 실패"); }
        finally { foreach(var r in imported)r.Dispose(); }
    }
    static string SafeName(string name) { var result=string.Concat(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c)).Trim().TrimEnd('.');return string.IsNullOrWhiteSpace(result)?"지출영수증":result; }
    bool HasImages() { if(receipts.Count>0)return true;MessageBox.Show(this,"먼저 영수증 이미지를 추가해 주세요.","영수증 추가");return false; }
    void Export()
    {
        if(!HasImages())return;
        RefreshPreview();
        using var dialog=new SaveFileDialog { Filter="PDF 문서 (*.pdf)|*.pdf|PNG 이미지 (*.png)|*.png|JPEG 이미지 (*.jpg)|*.jpg",FileName=SafeName(settings.Title),Title="전체 페이지 저장 · 이미지 여러 페이지는 번호를 붙여 저장" };
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        string extension=dialog.FilterIndex switch { 1=>".pdf",2=>".png",_=>".jpg" };
        string target=Path.ChangeExtension(dialog.FileName,extension);
        var paths=dialog.FilterIndex==1 || PageCount==1 ? new[]{target}:Enumerable.Range(1,PageCount).Select(i=>Path.Combine(Path.GetDirectoryName(target)!,Path.GetFileNameWithoutExtension(target)+$"_{i:00}"+extension)).ToArray();
        if(paths.Any(p=>File.Exists(p)&&p!=dialog.FileName) && MessageBox.Show(this,"같은 이름의 저장 파일이 있습니다. 덮어쓸까요?","파일 확인",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
        var staged=new List<(string temp,string path)>();
        try
        {
            UseWaitCursor=true;
            foreach(string path in paths) staged.Add((path+"."+Guid.NewGuid().ToString("N")+".tmp",path));
            if(dialog.FilterIndex==1) PdfExport.Write(staged[0].temp,receipts,settings);
            else for(int i=0;i<PageCount;i++)
            {
                using var image=ReceiptStudio.LayoutEngine.Render(pagePlan[i],settings,i,PageCount,300);
                if(dialog.FilterIndex==2)image.Save(staged[i].temp,ImageFormat.Png);
                else { using var output=File.Create(staged[i].temp);PdfExport.SaveJpeg(image,output); }
            }
            foreach(var item in staged)File.Move(item.temp,item.path,true);
            MessageBox.Show(this,$"{PageCount}페이지를 저장했습니다.\n\n{Path.GetDirectoryName(target)}","저장 완료");
        }
        catch(Exception ex) { MessageBox.Show(this,ex.Message,"파일 저장 실패"); }
        finally { UseWaitCursor=false;foreach(var item in staged)if(File.Exists(item.temp))File.Delete(item.temp); }
    }
    void Print()
    {
        if(!HasImages())return;
        RefreshPreview();
        using var document=new PrintDocument { DocumentName=settings.Title,OriginAtMargins=false };
        document.DefaultPageSettings.PaperSize=new PaperSize("A4",827,1169);
        document.DefaultPageSettings.Landscape=settings.Landscape;
        document.DefaultPageSettings.Margins=new Margins(0,0,0,0);
        document.PrinterSettings.MinimumPage=1;document.PrinterSettings.MaximumPage=PageCount;document.PrinterSettings.FromPage=1;document.PrinterSettings.ToPage=PageCount;
        using var dialog=new PrintDialog { Document=document,UseEXDialog=true,AllowSomePages=true,AllowSelection=false };
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        int current=document.PrinterSettings.PrintRange==PrintRange.SomePages ? document.PrinterSettings.FromPage-1 : 0;
        int last=document.PrinterSettings.PrintRange==PrintRange.SomePages ? Math.Min(document.PrinterSettings.ToPage,PageCount) : PageCount;
        // Keep the application preview's paper orientation even after printer preferences are edited.
        document.DefaultPageSettings.Landscape=settings.Landscape;
        document.PrintPage+=(_,e)=>
        {
            if(e.Graphics is null)return;
            using var image=ReceiptStudio.LayoutEngine.Render(pagePlan[current],settings,current,PageCount,300);
            e.Graphics.TranslateTransform(-e.PageSettings.HardMarginX,-e.PageSettings.HardMarginY);
            e.Graphics.DrawImage(image,new RectangleF(0,0,settings.Width/25.4f*100,settings.Height/25.4f*100));
            current++;e.HasMorePages=current<last;
        };
        try { document.Print(); } catch(Exception ex) { MessageBox.Show(this,ex.Message,"인쇄 실패"); }
    }
    protected override void Dispose(bool disposing) { if(disposing) { refreshTimer.Dispose();ClearReceipts(); } base.Dispose(disposing); }
}

public sealed partial class PreviewCanvas : Control
{
    Bitmap? page; bool empty;
    public PreviewCanvas() { DoubleBuffered=true;BackColor=Color.FromArgb(228,233,241);TabStop=true;SetStyle(ControlStyles.Selectable,true); }
    public void SetPage(Bitmap value,bool isEmpty,ReceiptPage layout,float paperWidth,float paperHeight,Receipt? selected)
    { CancelDrag();page?.Dispose();page=value;empty=isEmpty;placements=layout.Items.ToList();pageWidth=paperWidth;pageHeight=paperHeight;selectedReceipt=selected;Invalidate(); }
    public static RectangleF Fit(Size size,RectangleF bounds)
    { float scale=Math.Min(bounds.Width/size.Width,bounds.Height/size.Height);return new RectangleF(bounds.X+(bounds.Width-size.Width*scale)/2,bounds.Y+(bounds.Height-size.Height*scale)/2,size.Width*scale,size.Height*scale); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(page is null || Width<50 || Height<50)return;
        var rectangle=Fit(page.Size,new RectangleF(22,20,Width-44,Height-40));
        using var shadow=new SolidBrush(Color.FromArgb(196,204,217));e.Graphics.FillRectangle(shadow,rectangle.X+5,rectangle.Y+6,rectangle.Width,rectangle.Height);
        e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.DrawImage(page,rectangle);
        DrawSelection(e.Graphics);
        if(empty)
        {
            using var font=new Font("Malgun Gothic",13,FontStyle.Regular);
            using var format=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center };
            e.Graphics.DrawString("영수증을 추가해 주세요\n\n왼쪽의 ‘이미지 추가’를 누르거나\n이미지 파일을 창에 끌어놓으세요.",font,Brushes.SlateGray,rectangle,format);
        }
    }
    protected override void Dispose(bool disposing) { if(disposing)page?.Dispose();base.Dispose(disposing); }
}

public sealed class CropDialog : Form
{
    readonly CropSurface surface;
    public Bitmap? Cropped { get; private set; }
    public CropDialog(Bitmap source)
    {
        Text="영수증 자르기 · 마우스로 남길 영역을 드래그하세요";Size=new Size(860,780);MinimumSize=new Size(500,500);StartPosition=FormStartPosition.CenterParent;Font=new Font("Malgun Gothic",10);
        surface=new CropSurface(source) { Dock=DockStyle.Fill };Controls.Add(surface);
        var bottom=new FlowLayoutPanel { Dock=DockStyle.Bottom,Height=58,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8) };
        var apply=new Button { Text="선택 영역 적용",Width=150,Height=35 };apply.Click+=(_,_)=> { var area=surface.SelectedPixels;if(area.Width<10||area.Height<10){MessageBox.Show(this,"이미지에서 남길 영역을 드래그해 주세요.");return;} Cropped=source.Clone(area,PixelFormat.Format24bppRgb);DialogResult=DialogResult.OK; };
        bottom.Controls.Add(apply);bottom.Controls.Add(new Button { Text="취소",DialogResult=DialogResult.Cancel,Width=90,Height=35 });Controls.Add(bottom);
    }
}
public sealed class CropSurface : Control
{
    readonly Bitmap source; PointF start,end;bool dragging,selected;
    RectangleF ImageBounds=>PreviewCanvas.Fit(source.Size,new RectangleF(15,15,Math.Max(1,Width-30),Math.Max(1,Height-30)));
    public CropSurface(Bitmap image) { source=image;DoubleBuffered=true;BackColor=Color.FromArgb(224,230,239);Cursor=Cursors.Cross; }
    PointF Normalize(Point point) { var r=ImageBounds;return new PointF(Math.Clamp((point.X-r.X)/r.Width,0,1),Math.Clamp((point.Y-r.Y)/r.Height,0,1)); }
    public Rectangle SelectedPixels { get { int x=(int)(Math.Min(start.X,end.X)*source.Width),y=(int)(Math.Min(start.Y,end.Y)*source.Height);int right=(int)(Math.Max(start.X,end.X)*source.Width),bottom=(int)(Math.Max(start.Y,end.Y)*source.Height);return selected?new Rectangle(x,y,right-x,bottom-y):Rectangle.Empty; } }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e);if(e.Button!=MouseButtons.Left||!ImageBounds.Contains(e.Location))return;start=end=Normalize(e.Location);dragging=selected=true;Capture=true;Invalidate(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e);if(dragging){end=Normalize(e.Location);Invalidate();} }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e);if(dragging){end=Normalize(e.Location);dragging=false;Capture=false;Invalidate();} }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var r=ImageBounds;e.Graphics.DrawImage(source,r);
        if(!selected)return;
        var selection=new RectangleF(r.X+Math.Min(start.X,end.X)*r.Width,r.Y+Math.Min(start.Y,end.Y)*r.Height,Math.Abs(start.X-end.X)*r.Width,Math.Abs(start.Y-end.Y)*r.Height);
        using var overlay=new SolidBrush(Color.FromArgb(115,20,30,45));using var region=new Region(r);region.Exclude(selection);e.Graphics.FillRegion(overlay,region);
        using var pen=new Pen(Color.FromArgb(37,99,235),2);e.Graphics.DrawRectangle(pen,selection.X,selection.Y,selection.Width,selection.Height);
    }
}


