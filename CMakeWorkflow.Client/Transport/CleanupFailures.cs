using System.Runtime.ExceptionServices;

namespace CMakeWorkflow.Client.Transport;

// Use at teardown boundaries where every resource must be attempted, even if an
// earlier Dispose fails. Preserve the first exception and append cleanup errors
// in the order they occurred (the same policy as WorkflowProcessBackend).
internal sealed class CleanupFailures
{
    private readonly List<Exception> failures = [];

    internal void Add(Exception failure) => failures.Add(failure);

    internal void Dispose(IDisposable? resource)
    {
        if (resource is not null)
        {
            Attempt(resource.Dispose);
        }
    }

    internal async ValueTask DisposeAsync(IAsyncDisposable? resource)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            await resource.DisposeAsync();
        }
#pragma warning disable CA1031 // Cleanup must attempt later resources even when an arbitrary DisposeAsync fails.
        catch (Exception error) when (error is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            failures.Add(error);
        }
    }

    internal void Attempt(Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // Teardown records arbitrary cleanup failures instead of skipping later resources.
        catch (Exception error) when (error is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            failures.Add(error);
        }
    }

    internal async Task AttemptAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // Teardown records arbitrary cleanup failures instead of skipping later resources.
        catch (Exception error) when (error is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            failures.Add(error);
        }
    }

    internal void ThrowIfAny(string message)
    {
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(message, failures);
    }
}
