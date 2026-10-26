using System;
internal static class WheelChainModelTests {
 static void Check(bool yes,string name){if(!yes)throw new Exception(name);Console.WriteLine("PASS "+name);}
 public static int Main(){try{
  var r=new WheelRemainder();Check(r.Native(-30)==0&&r.Pending==-30,"native partial retained");Check(r.Native(-90)==-120&&r.Pending==0,"native receives whole notch only");
  r.Native(-90);Check(r.Outer(-30)==-120&&r.Pending==0,"boundary hands partial and new delta once");Check(r.Outer(-30)==-30,"no duplicate after hand-off");
  r.Native(-90);Check(r.Native(30)==0&&r.Pending==30,"reverse cancels old unexecuted intent");Check(r.Native(90)==120&&r.Pending==0,"reverse reaches correct native notch");
  r.Native(-90);r.Reset();Check(r.Outer(-30)==-30,"external change clears stale partial");
  r.Native(-30);Check(r.Native(0)==0&&r.Pending==-30,"zero delta preserves partial");
  r.Reset();int sum=0;for(int i=0;i<120;i++)sum+=r.Native(-1);Check(sum==-120&&r.Pending==0,"high precision native conservation");
  r.Native(-90);int whole=r.Native(-60);Check(whole==-120&&r.Drain()==-30&&r.Pending==0,"edge residual is distinct from consumed notch");
  r.Native(-119);whole=r.Native(-32768);Check(whole%120==0&&Math.Abs(r.Pending)<120&&whole+r.Pending==-32887,"extreme signed delta uses safe whole-notch slicing");
  Console.WriteLine("All wheel-chain model checks passed.");return 0;
 }catch(Exception error){Console.WriteLine("FAIL "+error.Message);return 1;}}
}
