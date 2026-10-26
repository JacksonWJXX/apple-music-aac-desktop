using System;
using System.Text;

// Compile with the production source and /main:QueueTests. No network, account,
// file system state, workers or authentication are used by these model tests.
internal static class QueueTests {
    private static void Check(bool condition,string name){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
    public static int Main(){try{
        SongQueue queue=new SongQueue();
        var added=queue.AddLines("https://music.apple.com/us/song/title/1\r\nhttps://music.apple.com/cn/album/title/2?i=1\nhttps://music.apple.com/us/song/2\nhttps://other.invalid/song/3\nhttps://music.apple.com/us/album/4\n\n");
        Check(added.Added==2&&added.Duplicate==1&&added.Invalid==2,"mixed input, canonical ID duplicates and invalid URLs");
        Check(queue.Items[0].Url=="https://music.apple.com/us/song/1","canonical URL contains no arbitrary query");
        var first=queue.TakeNext();Check(first.Id=="1"&&queue.TakeNext()==null,"at most one active item");
        queue.ClearFinished();Check(queue.Items.Count==2&&queue.Current==first,"clear preserves active and waiting");
        queue.Finish("error");Check(first.State=="error"&&first.Progress!="100%","failure is terminal without completion percent");
        var second=queue.TakeNext();Check(second.Id=="2","failure does not block the next item");
        queue.Finish("success");Check(second.Progress=="100%"&&!queue.HasWaiting&&queue.Current==null,"verified completion and queue drain");
        Check(queue.AddLines("https://music.apple.com/us/song/1").Duplicate==1,"repeated starts do not duplicate ended items");
        queue.ClearFinished();Check(queue.Items.Count==0&&queue.AddLines("https://music.apple.com/us/song/1").Added==1,"explicit clear allows retry");
        queue.AddLines("https://music.apple.com/us/song/3");first=queue.TakeNext();queue.CancelWaiting();Check(queue.Current==first&&queue.Items[1].State=="cancelled","cancel pending preserves active until worker cleanup");queue.Finish("cancelled");Check(queue.TakeNext()==null&&!queue.HasWaiting,"cancel all prevents next work");
        var text=new StringBuilder();for(int i=1;i<=102;i++)text.Append("https://music.apple.com/us/song/").Append(i).Append('\n');queue=new SongQueue();added=queue.AddLines(text.ToString());Check(added.Added==100&&added.Full==2,"bounded queue");
        Check(queue.AddLines(new string('x',65537)).Invalid==1,"bounded paste length");
        Console.WriteLine("All offline queue model tests passed.");return 0;
    }catch(Exception error){Console.WriteLine("FAIL "+error.Message);return 1;}}
}
