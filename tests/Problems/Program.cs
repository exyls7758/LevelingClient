using Multiverse.Services;
using System.Text.Json;
static void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
var root=Path.Combine(Path.GetTempPath(),"Leveling-Problems-"+Guid.NewGuid().ToString("N"));
try {
 var j=new ProblemJournal(root,2048);
 var error=new InvalidOperationException("secret C:\\Users\\Alice 100.1.2.3 pairing 123456",new IOException("secret token"));
 j.Record("AUDIO_FAILURE",error);
 string history=j.Read();
 Check(history.Contains("InvalidOperationException") && history.Contains("IOException"),"exception chain and types retained");
 Check(!history.Contains("secret") && !history.Contains("Alice") && !history.Contains("123456"),"messages paths addresses and tokens excluded");
 Check(new ProblemJournal(root,2048).Read()==history,"history survives application restart");
 for(int i=0;i<60;i++)j.Record("TEST_"+i,new Exception("secret"));
 Check(j.Read().Length<6000,"disk history remains bounded through rotation");
 foreach(string line in j.Read().Split('\n',StringSplitOptions.RemoveEmptyEntries))using(JsonDocument.Parse(line)){}
 Check(true,"rotated history remains valid JSONL");
 var broken=Path.Combine(root,"blocked");File.WriteAllText(broken,"x");new ProblemJournal(broken).Record("FAIL",error);
 Check(true,"journal failure never masks original exception");
}finally{if(Directory.Exists(root))Directory.Delete(root,true);}
