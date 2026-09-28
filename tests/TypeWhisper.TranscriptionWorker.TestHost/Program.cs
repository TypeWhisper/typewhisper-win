// Stands in for TypeWhisper.exe in worker mode, so tests start real worker processes without WinUI.
return TypeWhisper.PluginHost.TranscriptionWorkerServer.IsWorkerInvocation(args)
    ? TypeWhisper.PluginHost.TranscriptionWorkerServer.Run(args)
    : 2;
