using System.Reflection;

namespace ReceiptStudio;

internal static class InteractionTests
{
    public static int Run()
    {
        int checks=0;void Assert(bool value,string message){checks++;if(!value)throw new Exception(message);}
        var rect=new RectangleF(100,80,120,240);
        for(int corner=0;corner<4;corner++)
        {
            int sx=corner is 0 or 2?-1:1,sy=corner is 0 or 1?-1:1;
            Assert(ResizeGeometry.WidthFromDrag(60,rect,new PointF(40*sx,0),corner)==80,"Horizontal resize");
            Assert(ResizeGeometry.WidthFromDrag(60,rect,new PointF(0,80*sy),corner)==80,"Vertical resize");
            var resized=ResizeGeometry.Resize(rect,80f/60,corner);
            Assert(Math.Abs(resized.Width/resized.Height-.5)<.001,"Resize aspect ratio");
            Assert(Math.Abs((sx<0?resized.Right:resized.Left)-(sx<0?rect.Right:rect.Left))<.001,"Opposite X anchor");
            Assert(Math.Abs((sy<0?resized.Bottom:resized.Top)-(sy<0?rect.Bottom:rect.Top))<.001,"Opposite Y anchor");
        }
        Assert(ResizeGeometry.WidthFromDrag(60,rect,new PointF(10000,10000),3)==150,"Maximum width");
        Assert(ResizeGeometry.WidthFromDrag(60,rect,new PointF(-10000,-10000),3)==30,"Minimum width");
        using var first=new Receipt("first",new Bitmap(400,800));using var second=new Receipt("second",new Bitmap(400,600));
        var settings=new PageSettings();var layout=LayoutEngine.Plan([first,second],settings)[0];
        using var form=new Form { Size=new Size(900,700),ShowInTaskbar=false };
        using var canvas=new PreviewCanvas { Size=new Size(850,620) };form.Controls.Add(canvas);
        _=canvas.Handle;
        var bitmap=LayoutEngine.Render(layout,settings,0,1,110);var imageSize=bitmap.Size;
        canvas.SetPage(bitmap,false,layout,settings.Width,settings.Height,null);
        Receipt? selected=null;int committed=0;
        canvas.SelectionChanged+=r=>selected=r;
        canvas.WidthCommitted+=(r,w)=> { Assert(ReferenceEquals(r,second),"Resized wrong receipt");committed=w;r.PrintWidthMm=w; };
        var paper=PreviewCanvas.Fit(imageSize,new RectangleF(22,20,canvas.Width-44,canvas.Height-40));
        var hit=ResizeGeometry.Map(layout.Items[1].Bounds,paper,settings.Width,settings.Height);
        void Mouse(string method,Point point) => typeof(PreviewCanvas).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(canvas,[new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0)]);
        Mouse("OnMouseDown",Point.Round(new PointF(hit.X+hit.Width/2,hit.Y+hit.Height/2)));
        Assert(ReferenceEquals(selected,second),"Preview click selection");Assert(committed==0 && second.PrintWidthMm==0,"Click must not resize");
        var start=Point.Round(new PointF(hit.Right,hit.Bottom));var end=new Point(start.X+(int)Math.Round(hit.Width/3),start.Y);
        Mouse("OnMouseDown",start);Mouse("OnMouseMove",end);
        Assert(committed==0 && second.PrintWidthMm==0,"Drag must not reflow before release");
        Mouse("OnMouseUp",end);Assert(committed==80 && second.PrintWidthMm==80,"Release commits physical width");
        var updated=LayoutEngine.Plan([first,second],settings);Assert(updated.SelectMany(p=>p.Items).First(i=>i.Receipt==second).Bounds.Width==80,"Print layout follows drag");
        // Reload cancels capture without accidentally committing the pending pointer movement.
        committed=0;canvas.SetPage(LayoutEngine.Render(layout,settings,0,1,110),false,layout,settings.Width,settings.Height,second);
        Mouse("OnMouseDown",start);Mouse("OnMouseMove",end);canvas.SelectReceipt(first);Mouse("OnMouseUp",end);
        Assert(committed==0,"Cancelled capture must not commit");
        canvas.SetPage(LayoutEngine.Render(layout,settings,0,1,110),false,layout,settings.Width,settings.Height,second);
        Mouse("OnMouseDown",start);Mouse("OnMouseMove",end);
        object[] keyArgs=[Message.Create(canvas.Handle,0,IntPtr.Zero,IntPtr.Zero),Keys.Escape];
        bool cancelled=(bool)typeof(PreviewCanvas).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(canvas,keyArgs)!;
        Mouse("OnMouseUp",end);Assert(cancelled && committed==0,"Escape cancels without changing output");
        return checks;
    }
}
