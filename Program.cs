using CMakeWorkflow.Commands;

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += OnCancel;
try
{
    return await CliApplication.ExecuteAsync(args, cancellation.Token);
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("[CMakeWorkflow] Canceled.");
    return 130;
}
catch (Exception error) when (CliErrors.IsExpected(error))
{
    await Console.Error.WriteLineAsync($"[CMakeWorkflow] {error.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= OnCancel;
}

void OnCancel(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    cancellation.Cancel();
}
