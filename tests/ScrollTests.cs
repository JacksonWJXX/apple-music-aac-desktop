using System;

// Pure motion model; no windows, input injection, user state or network access.
internal static class ScrollTests {
    static void Check(bool yes,string name){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);}
    static void Near(double a,double b,string name){Check(Math.Abs(a-b)<0.001,name);}
    public static int Main(){try{
        foreach(int dpi in new[]{96,120,144,192}){
            var m=new ScrollMotion();m.Reset(0,10000);m.Wheel(-120,dpi,3,500,10000);Near(m.Target,24*dpi/96.0,"one notch scales once at DPI "+dpi);
            var fine=new ScrollMotion();fine.Reset(0,10000);for(int i=0;i<120;i++)fine.Wheel(-1,dpi,3,500,10000);Near(fine.Target,m.Target,"high resolution delta accumulation "+dpi);
            int previous=0;bool monotonic=true;for(int i=0;i<30;i++){int next=m.Advance(16,10000);monotonic&=next>=previous;previous=next;}Check(monotonic,"monotonic animation");Check(!m.Moving,"bounded settle time");
        }
        var motion=new ScrollMotion();motion.Reset(0,1000);motion.Wheel(-120,144,0,500,1000);Near(motion.Target,0,"disabled wheel preference");
        motion.Wheel(-120,144,-1,500,1000);Near(motion.Target,464,"one page preference");
        motion.Reset(100,1000);motion.Wheel(-120,144,3,500,1000);motion.Advance(16,1000);double before=motion.Position;motion.Wheel(120,144,3,500,1000);motion.Advance(16,1000);Check(motion.Position<before,"reverse immediately cancels old direction");
        motion.Reset(0,200);for(int i=0;i<10000;i++)motion.Wheel(-120,144,3,500,200);Near(motion.Target,200,"burst coalesces into one clamped target");motion.Finish();motion.Wheel(120,144,3,500,200);Near(motion.Target,164,"bottom boundary has no queued overshoot");
        motion.Reset(0,200);for(int i=0;i<100;i++)motion.Wheel(120,144,3,500,200);motion.Wheel(-120,144,3,500,200);Near(motion.Target,36,"top boundary has no queued overshoot");
        motion.Reset(190,200);motion.Wheel(-120,144,3,500,200);motion.Advance(16,50);Check(motion.Target<=50&&motion.Position<=50,"content resize clamps both positions");
        motion.Reset(27,1000);Near(motion.Target,27,"external navigation discards old motion");
        motion.Reset(0,1000);motion.Wheel(-120,144,3,500,1000);Check(motion.Finish()==36&&!motion.Moving,"reduced animation finishes once");
        Console.WriteLine("All offline scroll model checks passed.");return 0;
    }catch(Exception error){Console.WriteLine("FAIL "+error.Message);return 1;}}
}
