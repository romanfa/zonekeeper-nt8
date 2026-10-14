// PURE: the same queue boundary is used by the engine and deterministic tests.
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns.ZoneKeeper
{
    public sealed class AccountSnapshot
    {
        private readonly List<PositionSnapshot> positions;
        private readonly List<ExecutionEvidence> executions;
        public bool Consistent { get; private set; }
        public List<PositionSnapshot> Positions { get { return positions.Select(CopyPosition).ToList(); } }
        public List<ExecutionEvidence> Executions { get { return executions.Select(x => x.Copy()).ToList(); } }
        public List<string> ExecutionIds { get { return executions.Select(x => x.ExecId).ToList(); } }
        public AccountSnapshot(IEnumerable<PositionSnapshot> current, IEnumerable<ExecutionEvidence> history, bool consistent = true)
        {
            positions = (current ?? new PositionSnapshot[0]).Select(CopyPosition).ToList();
            executions = (history ?? new ExecutionEvidence[0]).Where(x => x != null).Select(x => x.Copy()).ToList();
            Consistent = consistent;
        }
        private static PositionSnapshot CopyPosition(PositionSnapshot p)
        { return new PositionSnapshot { Instrument = p.Instrument, Net = p.Net, AveragePrice = p.AveragePrice, PointValue = p.PointValue, Currency = p.Currency }; }
        public AccountSnapshot Uncertain() { return new AccountSnapshot(positions, executions, false); }
        public static AccountSnapshot Capture(Func<List<PositionSnapshot>> readPositions, Func<List<ExecutionEvidence>> readExecutions)
        {
            // Separate NT collection locks do not promise a shared transaction. A changing history is unsafe.
            var before = readExecutions(); var current = readPositions(); var after = readExecutions();
            bool stable = before.Count == after.Count && before.Zip(after, (a, b) => a.Matches(b)).All(x => x);
            if (after.Any(x => !x.Complete) || after.Select(x => x.ExecId).Distinct(StringComparer.Ordinal).Count() != after.Count) stable = false;
            foreach (var group in after.GroupBy(x => x.Instrument))
            {
                var history = group.ToList();
                if (history.Zip(history.Skip(1), (a, b) => a.TimeMs <= b.TimeMs).Any(x => !x)) stable = false;
                var position = current.FirstOrDefault(x => x.Instrument == group.Key);
                if (history.Last().PositionAfter != (position == null ? 0 : position.Net)) stable = false;
            }
            return new AccountSnapshot(current, after, stable);
        }
    }
    public static class ResyncBoundary
    {
        public static void Enqueue<T>(T source, Action<Action> enqueue, Func<T, AccountSnapshot> capture,
            Action beforeApply, Action<T, AccountSnapshot> apply)
        {
            // Capture now, before either the startup work or the reconnect refresh reaches the worker.
            var snapshot = capture(source);
            enqueue(() => {
                if (beforeApply != null) beforeApply();
                apply(source, snapshot);
            });
        }
    }
    public sealed class AccountEventBoundary<T> where T : class
    {
        private readonly object gate = new object();
        private readonly Dictionary<T, long> revisions = new Dictionary<T, long>();
        private readonly Action<Action> enqueue;
        public AccountEventBoundary(Action<Action> queue) { enqueue = queue; }
        private long Revision(T source)
        { long value; return revisions.TryGetValue(source, out value) ? value : 0; }
        public void Fill(T source, Action apply)
        {
            lock (gate) { revisions[source] = Revision(source) + 1; enqueue(apply); }
        }
        public void Request(T source, Action<Action> background, Func<T, AccountSnapshot> capture, Action beforeApply,
            Action<T, AccountSnapshot> apply)
        {
            background(() => {
                long started; lock (gate) started = Revision(source);
                var snapshot = capture(source);
                lock (gate)
                {
                    long cutoff = Revision(source);
                    if (cutoff != started) snapshot = snapshot.Uncertain();
                    ResyncBoundary.Enqueue(source, enqueue, ignored => snapshot, beforeApply, (current, captured) => {
                        // Callbacks queued before capture are ahead of this apply; later callbacks are behind it.
                        apply(current, captured);
                    });
                }
            });
        }
    }
}
