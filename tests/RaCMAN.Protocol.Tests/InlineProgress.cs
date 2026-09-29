namespace RaCMAN.Protocol.Tests;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its callback on the spot. <see cref="Progress{T}"/>
/// posts every report to the synchronisation context it was made on, which under xUnit is a pool
/// of threads shared by every test class running at the same time: a report could still be queued
/// when the call that made it had returned, and two could run at once. A test that asserts on what
/// was reported needs the report to have happened, in order, by the time the call returns.
/// </summary>
internal sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report) => _report = report;

    public void Report(T value) => _report(value);
}
