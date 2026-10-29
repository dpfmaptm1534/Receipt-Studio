using System.Drawing.Drawing2D;

namespace ReceiptStudio;

public static class ResizeGeometry
{
    // Signed relative movement permits horizontal, vertical and diagonal drags.
    public static int WidthFromDrag(float width,RectangleF rectangle,PointF delta,int corner)
    {
        float horizontal=delta.X*(corner is 0 or 2?-1:1)/rectangle.Width;
        float vertical=delta.Y*(corner is 0 or 1?-1:1)/rectangle.Height;
        float change=Math.Abs(horizontal)>=Math.Abs(vertical)?horizontal:vertical;
        return (int)Math.Clamp(Math.Round(width*(1+change)),30,150);
    }
    public static RectangleF Resize(RectangleF rectangle,float scale,int corner)
    {
        float width=rectangle.Width*scale,height=rectangle.Height*scale;
        return new RectangleF(corner is 0 or 2?rectangle.Right-width:rectangle.Left,corner is 0 or 1?rectangle.Bottom-height:rectangle.Top,width,height);
    }
    public static RectangleF Map(RectangleF bounds,RectangleF paper,float width,float height)
        =>new(paper.X+bounds.X/width*paper.Width,paper.Y+bounds.Y/height*paper.Height,bounds.Width/width*paper.Width,bounds.Height/height*paper.Height);
}

public sealed partial class PreviewCanvas
{
    List<ReceiptPlacement> placements=[];
    Receipt? selectedReceipt;
    float pageWidth=210,pageHeight=297;
    ReceiptPlacement? draggingItem;
    RectangleF startBounds;
    Point dragStart;
    int dragCorner,dragWidth;
    public event Action<Receipt?>? SelectionChanged;
    public event Action<Receipt,int>? WidthCommitted;
    float HandleSize=>10*DeviceDpi/96f;
    RectangleF PaperBounds=>page is null?RectangleF.Empty:Fit(page.Size,new RectangleF(22,20,Math.Max(1,Width-44),Math.Max(1,Height-40)));
    RectangleF ScreenBounds(ReceiptPlacement item)=>ResizeGeometry.Map(item.Bounds,PaperBounds,pageWidth,pageHeight);
    public void SelectReceipt(Receipt? receipt) { CancelDrag();selectedReceipt=receipt;Invalidate(); }
    static PointF[] Corners(RectangleF r)=>[new(r.Left,r.Top),new(r.Right,r.Top),new(r.Left,r.Bottom),new(r.Right,r.Bottom)];
    int HandleAt(RectangleF rectangle,Point point)
    {
        var corners=Corners(rectangle);float radius=HandleSize*.8f;
        for(int i=0;i<4;i++)if(new RectangleF(corners[i].X-radius,corners[i].Y-radius,2*radius,2*radius).Contains(point))return i;
        return -1;
    }
    void CancelDrag() { draggingItem=null;if(Capture)Capture=false;Cursor=Cursors.Default;Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left || page is null)return;Focus();
        foreach(var item in placements.Where(i=>ReferenceEquals(i.Receipt,selectedReceipt)))
        {
            var rect=ScreenBounds(item);int corner=HandleAt(rect,e.Location);
            if(corner<0)continue;
            draggingItem=item;startBounds=rect;dragStart=e.Location;dragCorner=corner;dragWidth=(int)Math.Round(item.Bounds.Width);Capture=true;Invalidate();return;
        }
        var hit=placements.LastOrDefault(i=>ScreenBounds(i).Contains(e.Location));
        selectedReceipt=hit?.Receipt;SelectionChanged?.Invoke(selectedReceipt);Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(draggingItem is not null)
        {
            dragWidth=ResizeGeometry.WidthFromDrag(draggingItem.Bounds.Width,startBounds,new PointF(e.X-dragStart.X,e.Y-dragStart.Y),dragCorner);
            Invalidate();return;
        }
        foreach(var item in placements.Where(i=>ReferenceEquals(i.Receipt,selectedReceipt)))
        { int corner=HandleAt(ScreenBounds(item),e.Location);if(corner>=0){Cursor=corner is 0 or 3?Cursors.SizeNWSE:Cursors.SizeNESW;return;} }
        Cursor=placements.Any(i=>ScreenBounds(i).Contains(e.Location))?Cursors.Hand:Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);if(e.Button!=MouseButtons.Left || draggingItem is null)return;
        var item=draggingItem;
        int width=ResizeGeometry.WidthFromDrag(item.Bounds.Width,startBounds,new PointF(e.X-dragStart.X,e.Y-dragStart.Y),dragCorner);
        CancelDrag();
        if(width!=(int)Math.Round(item.Bounds.Width))WidthCommitted?.Invoke(item.Receipt,width);
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    { if(keyData==Keys.Escape && draggingItem is not null){CancelDrag();return true;}return base.ProcessCmdKey(ref msg,keyData); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e);if(!Capture && draggingItem is not null)CancelDrag(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e);CancelDrag(); }
    void DrawSelection(Graphics graphics)
    {
        foreach(var item in placements.Where(i=>ReferenceEquals(i.Receipt,selectedReceipt)))
        {
            var rect=ScreenBounds(item);bool dragging=ReferenceEquals(item,draggingItem);
            if(dragging)
            {
                graphics.FillRectangle(Brushes.White,RectangleF.Inflate(rect,1,1));
                rect=ResizeGeometry.Resize(startBounds,dragWidth/item.Bounds.Width,dragCorner);
                var state=graphics.Save();graphics.SetClip(PaperBounds,CombineMode.Intersect);
                graphics.DrawImage(item.Receipt.Image,rect,item.Source,GraphicsUnit.Pixel);graphics.Restore(state);
            }
            using var pen=new Pen(Color.FromArgb(37,99,235),2*DeviceDpi/96f);if(dragging)pen.DashStyle=DashStyle.Dash;
            graphics.DrawRectangle(pen,rect.X,rect.Y,rect.Width,rect.Height);
            foreach(var point in Corners(rect))
            { var handle=new RectangleF(point.X-HandleSize/2,point.Y-HandleSize/2,HandleSize,HandleSize);graphics.FillRectangle(Brushes.White,handle);graphics.DrawRectangle(pen,handle.X,handle.Y,handle.Width,handle.Height); }
            string label=dragging?$"{dragWidth} mm · 놓으면 자동 배치 · Esc 취소":$"{item.Bounds.Width:0} mm · 모서리를 드래그";
            using var font=new Font("Malgun Gothic",9);
            var size=graphics.MeasureString(label,font);float x=Math.Clamp(rect.X,4,Math.Max(4,Width-size.Width-12));float y=Math.Clamp(rect.Y-size.Height-10,4,Math.Max(4,Height-size.Height-8));
            using var background=new SolidBrush(Color.FromArgb(37,99,235));graphics.FillRectangle(background,x,y,size.Width+10,size.Height+6);graphics.DrawString(label,font,Brushes.White,x+5,y+3);
        }
    }
}
