using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HD2ModCore.Infrastructure.Diagnostics;

namespace HD2ModManager.Services;

internal static class DiagnosticOperations
{
	internal static DiagnosticJournal Journal { get; } = new(Path.Combine(AppContext.BaseDirectory, "logs", "diagnostic-events.jsonl"));

	internal static Operation Begin(DiagnosticAction action, DiagnosticTrigger trigger = DiagnosticTrigger.User,
		Guid? profile = null, long? revision = null, Guid? mod = null, Guid? parent = null, string? code = null,
		CancellationToken cancellationToken = default)
		=> new(action, trigger, profile, revision, mod, parent, code, cancellationToken);

	internal sealed class Operation : IDisposable
	{
		private readonly DiagnosticAction action;
		private readonly DiagnosticTrigger trigger;
		private readonly Guid? profile;
		private readonly long? revision;
		private readonly Guid? mod;
		private readonly Guid? parent;
		private readonly CancellationToken cancellationToken;
		private readonly long started = Stopwatch.GetTimestamp();
		private bool finished;
		internal Guid Id { get; }

		internal Operation(DiagnosticAction action, DiagnosticTrigger trigger, Guid? profile, long? revision,
			Guid? mod, Guid? parent, string? code, CancellationToken cancellationToken)
		{
			this.action = action;
			this.trigger = trigger;
			this.profile = profile;
			this.revision = revision;
			this.mod = mod;
			this.parent = parent;
			this.cancellationToken = cancellationToken;
			Id = Journal.Record(action, DiagnosticResult.Started, trigger, profile: profile, revision: revision, mod: mod, parent: parent, code: code);
		}

		internal void Finish(DiagnosticResult result, string? code = null, int? count = null,
			Exception? exception = null, long? targetRevision = null)
		{
			if (finished) return;
			finished = true;
			Journal.Record(action, result, trigger, Id, parent, profile, targetRevision ?? revision, mod,
				count, code ?? exception?.GetType().Name, exception?.HResult, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
		}

		public void Dispose()
		{
			if (cancellationToken.IsCancellationRequested) Finish(DiagnosticResult.Canceled);
			else Finish(DiagnosticResult.Failed, "OutcomeUnavailable");
		}
	}
}
