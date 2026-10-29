namespace ReceiptStudio;

public sealed partial class MainForm
{
    bool ocrBusy;
    CancellationTokenSource? ocrCancellation;
    readonly List<Button> ocrActions=[];
    readonly List<Button> blockedDuringOcr=[];
    Button BlockDuringOcr(Button button) { blockedDuringOcr.Add(button);return button; }
    readonly Label ocrMessage=new();
    Button? stopOcr;
    Control BuildOcrToolbar()
    {
        var panel=new TableLayoutPanel { Dock=DockStyle.Fill,RowCount=2,ColumnCount=1,BackColor=Color.White,Margin=new Padding(0) };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute,47));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0),Padding=new Padding(7,0,0,0),AutoScroll=true };
        void Add(string text,EventHandler click,int width=105,bool primary=false) { var b=Button(text,click,primary,width);buttons.Controls.Add(b);ocrActions.Add(b); }
        Add("전체 OCR",async (_,_)=>await RecognizeReceipts(false),100,true);
        Add("선택 OCR",async (_,_)=>await RecognizeReceipts(true),100);
        Add("날짜 확인/수정",(_,_)=>EditDate(),125);
        Add("날짜 ↑",(_,_)=>SortDates(false),88);
        Add("날짜 ↓",(_,_)=>SortDates(true),88);
        stopOcr=Button("중지",(_,_)=> { ocrCancellation?.Cancel();ocrMessage.Text="현재 이미지 처리가 끝나면 중지합니다…"; },false,65);stopOcr.Enabled=false;buttons.Controls.Add(stopOcr);
        ocrMessage.Text="날짜 ↑ 오래된 순  ·  날짜 ↓ 최신 순  ·  날짜 없는 영수증은 맨 뒤  ·  OCR 날짜는 확인해 주세요";
        ocrMessage.Dock=DockStyle.Fill;ocrMessage.Padding=new Padding(12,0,5,0);ocrMessage.ForeColor=Color.FromArgb(90,107,128);ocrMessage.Font=new Font(Font.FontFamily,8.5f);
        panel.Controls.Add(buttons,0,0);panel.Controls.Add(ocrMessage,0,1);return panel;
    }
    async Task RecognizeReceipts(bool selectedOnly)
    {
        if(ocrBusy||!HasImages())return;
        if(selectedOnly && list.SelectedIndex<0) { MessageBox.Show(this,"먼저 왼쪽 목록에서 영수증을 선택해 주세요.");return; }
        var queue=selectedOnly?new List<Receipt>{receipts[list.SelectedIndex]}:receipts.ToList();
        ocrBusy=true;foreach(var button in ocrActions.Concat(blockedDuringOcr))button.Enabled=false;stopOcr!.Enabled=true;
        ocrCancellation=new CancellationTokenSource();int done=0,failed=0;string? failure=null;
        try
        {
            ocrMessage.Text="한글·영어 OCR 준비 중… (이 PC 안에서 처리합니다)";
            using var service=await Task.Run(()=>new ReceiptOcr());
            foreach(var receipt in queue)
            {
                if(ocrCancellation.IsCancellationRequested)break;
                ocrMessage.Text=$"OCR {done+1} / {queue.Count} · {receipt.Name} · 미리보기와 용지 설정을 계속 사용할 수 있습니다";
                using var copy=new Bitmap(receipt.Image);
                try
                {
                    var result=await Task.Run(()=>service.Recognize(copy));
                    receipt.OcrText=result.text;receipt.OcrStatus=result.reading.Status;receipt.DateCandidates=result.reading.Candidates;
                    if(!receipt.DateEdited)receipt.PaymentDate=result.reading.Date;
                }
                catch(Exception ex) { receipt.OcrStatus="인식 실패 · 재시도 필요";if(!receipt.DateEdited)receipt.PaymentDate=null;receipt.DateCandidates=[];receipt.OcrText="";failed++;failure=ex.Message; }
                done++;dirty=true;list.Invalidate();
            }
            int dates=queue.Take(done).Count(r=>r.PaymentDate.HasValue);
            ocrMessage.Text=$"{(ocrCancellation.IsCancellationRequested?"중지":"완료")} · {done}/{queue.Count}장 처리 · 날짜 {dates}장 · 미확인 {done-dates}장{(failed>0?$" · 실패 {failed}장":"")} · ‘날짜 확인/수정’ 후 정렬하세요";
            if(failed>0)MessageBox.Show(this,$"{failed}장의 OCR을 완료하지 못했습니다. 회전·자르기 후 다시 시도할 수 있습니다.\n\n{failure}","일부 OCR 실패");
        }
        catch(Exception ex)
        {
            ocrMessage.Text="OCR을 시작하지 못했습니다. 날짜 직접 입력과 정렬은 사용할 수 있습니다.";
            MessageBox.Show(this,"OCR 구성 요소를 불러오지 못했습니다.\n다른 PC에서는 Microsoft Visual C++ 2015–2022 재배포 가능 패키지(x64)가 필요할 수 있습니다.\n\n"+ex.GetBaseException().Message,"OCR 초기화 실패");
        }
        finally { ocrBusy=false;ocrCancellation.Dispose();ocrCancellation=null;foreach(var button in ocrActions.Concat(blockedDuringOcr))button.Enabled=true;stopOcr.Enabled=false;RefreshPreview(); }
    }
    void EditDate()
    {
        if(ocrBusy)return;
        int i=list.SelectedIndex;if(i<0){MessageBox.Show(this,"먼저 왼쪽 목록에서 영수증을 선택해 주세요.");return;}
        using var dialog=new DateEditDialog(receipts[i]);
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;
        receipts[i].PaymentDate=dialog.PaymentDate;receipts[i].DateEdited=true;ReloadList(i);
    }
    void SortDates(bool descending)
    {
        if(ocrBusy||!HasImages())return;
        if(receipts.All(r=>r.PaymentDate is null)) { MessageBox.Show(this,"날짜가 없습니다. ‘전체 OCR’을 실행하거나 ‘날짜 확인/수정’으로 결제일을 입력해 주세요.");return; }
        var selected=list.SelectedItem as Receipt;var sorted=ReceiptDates.Sort(receipts,descending);receipts.Clear();receipts.AddRange(sorted);
        ReloadList(selected is null?-1:receipts.IndexOf(selected));
        ocrMessage.Text=$"{(descending?"최신 날짜부터":"오래된 날짜부터")} 정렬했습니다 · 날짜 없음 {receipts.Count(r=>r.PaymentDate is null)}장은 맨 뒤 · 같은 날짜는 기존 순서 유지";
    }
}

public sealed class DateEditDialog : Form
{
    readonly DateTimePicker picker=new();
    public DateTime? PaymentDate=>picker.Checked?picker.Value.Date:null;
    public DateEditDialog(Receipt receipt)
    {
        Text="결제일 확인 · "+receipt.Name;Size=new Size(1020,760);MinimumSize=new Size(760,540);StartPosition=FormStartPosition.CenterParent;Font=new Font("Malgun Gothic",10);AutoScaleMode=AutoScaleMode.Dpi;
        var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(15) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,53));root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,47));Controls.Add(root);
        var image=new PictureBox { Dock=DockStyle.Fill,Image=receipt.Image,SizeMode=PictureBoxSizeMode.Zoom,BackColor=Color.FromArgb(236,240,246) };root.Controls.Add(image,0,0);
        var fields=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=8,Padding=new Padding(15,0,0,0) };
        foreach(int h in new[]{48,40,38,42,55,30})fields.RowStyles.Add(new RowStyle(SizeType.Absolute,h));fields.RowStyles.Add(new RowStyle(SizeType.Percent,100));fields.RowStyles.Add(new RowStyle(SizeType.Absolute,48));root.Controls.Add(fields,1,0);
        fields.Controls.Add(new Label { Text="영수증 원본의 결제일과 비교해 주세요.\n합본 이미지라면 먼저 각 영수증을 잘라 주세요.",Dock=DockStyle.Fill },0,0);
        picker.Format=DateTimePickerFormat.Custom;picker.CustomFormat="yyyy년 MM월 dd일";picker.ShowCheckBox=true;picker.Dock=DockStyle.Fill;picker.Value=receipt.PaymentDate??DateTime.Today;picker.Checked=receipt.PaymentDate.HasValue;fields.Controls.Add(picker,0,1);
        fields.Controls.Add(new Label { Text="OCR에서 찾은 날짜 후보",Dock=DockStyle.Fill,TextAlign=ContentAlignment.BottomLeft },0,2);
        var candidates=new ComboBox { Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList };
        candidates.Items.Add("후보를 선택하면 위 날짜에 반영됩니다");foreach(var date in receipt.DateCandidates)candidates.Items.Add(date.ToString("yyyy-MM-dd"));candidates.SelectedIndex=0;
        candidates.SelectedIndexChanged+=(_,_)=> { if(candidates.SelectedIndex>0){picker.Value=receipt.DateCandidates[candidates.SelectedIndex-1];picker.Checked=true;} };fields.Controls.Add(candidates,0,3);
        fields.Controls.Add(new Label { Text="체크를 해제하면 날짜 미지정으로 저장합니다.\n직접 확인한 날짜는 OCR을 다시 실행해도 유지됩니다.",Dock=DockStyle.Fill,ForeColor=Color.DimGray },0,4);
        fields.Controls.Add(new Label { Text="인식한 전체 내용 (참고용)",Dock=DockStyle.Fill },0,5);
        fields.Controls.Add(new TextBox { Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Text=receipt.OcrText.Replace("\r\n","\n").Replace("\n",Environment.NewLine),BackColor=Color.White },0,6);
        var actions=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft };
        actions.Controls.Add(new Button { Text="날짜 적용",DialogResult=DialogResult.OK,Width=110,Height=36 });actions.Controls.Add(new Button { Text="취소",DialogResult=DialogResult.Cancel,Width=80,Height=36 });fields.Controls.Add(actions,0,7);
    }
}
