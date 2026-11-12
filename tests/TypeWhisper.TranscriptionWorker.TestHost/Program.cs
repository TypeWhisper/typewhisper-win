// Stands in for TypeWhisper.exe in worker mode, so tests start real worker processes without WinUI.
// The tests name their temporary root; the worker accepts packages below it as the app does below its profile.
var root = Environment.GetEnvironmentVariable("TYPEWHISPER_TEST_WORKER_ROOT")
    ?? Path.Join(Path.GetTempPath(), "typewhisper-worker-tests-without-root");
var policy = new TypeWhisper.PluginHost.TranscriptionWorkerPackagePolicy(new HashSet<string> { "test.typewhisper.worker" },
    Path.Join(root, "store", "packages"), Path.Join(root, "bundled"));
return TypeWhisper.PluginHost.TranscriptionWorkerServer.IsWorkerInvocation(args)
    ? TypeWhisper.PluginHost.TranscriptionWorkerServer.Run(args, policy)
    : 2;
