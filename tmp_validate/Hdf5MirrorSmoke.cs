using System;
using System.IO;
using System.Linq;
using System.Reflection;
using DH.Driver.SDK;

var asm = Assembly.LoadFrom(@"D:\GIT-PROJECT\dhdas\tmp_validate\Hdf5MirrorValidation.dll");
var writerType = asm.GetType("DH.Client.App.Services.Storage.SdkRawCaptureHdf5MirrorWriter", throwOnError: true)!;
var writer = Activator.CreateInstance(writerType, nonPublic: true)!;

string basePath = @"D:\GIT-PROJECT\dhdas\tmp_validate\mirror_output";
if (Directory.Exists(basePath)) Directory.Delete(basePath, recursive: true);
Directory.CreateDirectory(basePath);

writerType.GetMethod("Start")!.Invoke(writer, new object[] { basePath, "session", 1000d });
var block = new SdkRawBlock
{
    SampleTime = 1,
    MessageType = 0,
    GroupId = 0,
    MachineId = 0,
    TotalDataCount = 4,
    DataCountPerChannel = 4,
    BufferCountBytes = 8 * sizeof(float),
    BlockIndex = 0,
    ChannelCount = 2,
    SampleRateHz = 1000f,
    ReceivedAtUtc = DateTime.UtcNow,
    InterleavedSamples = new[] { 1f, 10f, 2f, 20f, 3f, 30f, 4f, 40f },
    PayloadFloatCount = 8,
    ReturnBufferToPool = false
};
writerType.GetMethod("TryEnqueueClone")!.Invoke(writer, new object[] { block });
var result = writerType.GetMethod("Complete")!.Invoke(writer, null)!;
var resultType = result.GetType();
string root = (string)(resultType.GetProperty("OutputRootPath")!.GetValue(result) ?? "");
int fileCount = (int)(resultType.GetProperty("FileCount")!.GetValue(result) ?? 0);
bool faulted = (bool)(resultType.GetProperty("Faulted")!.GetValue(result) ?? false);
Console.WriteLine($"root={root}");
Console.WriteLine($"fileCount={fileCount}");
Console.WriteLine($"faulted={faulted}");
foreach (var file in Directory.EnumerateFiles(root, "*.h5", SearchOption.AllDirectories).OrderBy(x => x))
{
    Console.WriteLine(file);
}
