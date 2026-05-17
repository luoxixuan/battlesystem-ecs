using System;
using System.Diagnostics;
using System.IO;

var git = Process.Start(new ProcessStartInfo
{
    FileName = "git",
    Arguments = "-C F:\\AI\\BattleSystem-ECS cat-file -p HEAD:Core/GameManager.cs",
    RedirectStandardOutput = true,
    UseShellExecute = false
});

var bytes = new MemoryStream();
git!.StandardOutput.BaseStream.CopyTo(bytes);
File.WriteAllBytes("F:\\AI\\BattleSystem-ECS\\Core\\GameManager.cs", bytes.ToArray());

var buf = File.ReadAllBytes("F:\\AI\\BattleSystem-ECS\\Core\\GameManager.cs");
Console.WriteLine($"Written {buf.Length} bytes, BOM: {BitConverter.ToString(buf[..4])}");