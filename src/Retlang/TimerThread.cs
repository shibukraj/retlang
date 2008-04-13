using System;
using System.Collections.Generic;
using System.Threading;
using System.Timers;
using System.Diagnostics;

namespace Retlang
{
    public interface IPendingEvent : ITimerControl
    {
        /// <summary>
        /// Time of expiration for this event
        /// </summary>
        long Expiration { get; }

        /// <summary>
        /// Execute this event and optionally schedule another execution.
        /// </summary>
        /// <returns></returns>
        IPendingEvent Execute(long currentTime);
    }

    public class SingleEvent : IPendingEvent
    {
        private readonly ICommandQueue _queue;
        private readonly Command _toExecute;
        private readonly long _expiration;
        private bool _canceled;

        public SingleEvent(ICommandQueue queue, Command toExecute, long scheduledTimeInMs, long now)
        {
            _expiration = now + scheduledTimeInMs;
            _queue = queue;
            _toExecute = toExecute;
        }

        public long Expiration
        {
            get { return _expiration; }
        }

        public IPendingEvent Execute(long currentTime)
        {
            if (!_canceled)
            {
                _queue.Enqueue(_toExecute);
            }
            return null;
        }

        public void Cancel()
        {
            _canceled = true;
        }
    }

    internal class RecurringEvent : IPendingEvent
    {
        private readonly ICommandQueue _queue;
        private readonly Command _toExecute;
        private readonly long _regularInterval;
        private long _expiration;
        private bool _canceled;

        public RecurringEvent(ICommandQueue queue, Command toExecute, long scheduledTimeInMs, long regularInterval, long currentTime)
        {
            _expiration = currentTime + scheduledTimeInMs;
            _queue = queue;
            _toExecute = toExecute;
            _regularInterval = regularInterval;
        }

        private static DateTime CalculateExpiration(long scheduledTimeInMs)
        {
            return DateTime.Now.AddMilliseconds(scheduledTimeInMs);
        }

        public long Expiration
        {
            get { return _expiration; }
        }

        public IPendingEvent Execute(long currentTime)
        {
            if (!_canceled)
            {
                _queue.Enqueue(_toExecute);
                _expiration = currentTime +_regularInterval;
                return this;
            }
            return null;
        }

        public void Cancel()
        {
            _canceled = true;
        }
    }

    /// <summary>
    /// A Thread dedicated to event scheduling.
    /// </summary>
    public class TimerThread : IDisposable
    {

        private readonly SortedList<long, List<IPendingEvent>> _pending =
            new SortedList<long, List<IPendingEvent>>();

        private readonly Stopwatch _timer = Stopwatch.StartNew();

        private readonly AutoResetEvent _waiter = new AutoResetEvent(false);
        private RegisteredWaitHandle _cancel = null;
        private readonly object _lock = new object();
        private bool _running = true;

        public void Start()
        {
        }

        public ITimerControl Schedule(ICommandQueue targetQueue, Command toExecute, long scheduledTimeInMs)
        {
            SingleEvent pending = new SingleEvent(targetQueue, toExecute, scheduledTimeInMs, _timer.ElapsedMilliseconds);
            QueueEvent(pending);
            return pending;
        }

        public ITimerControl ScheduleOnInterval(ICommandQueue queue, Command toExecute, long scheduledTimeInMs,
                                                long intervalInMs)
        {
            RecurringEvent pending = new RecurringEvent(queue, toExecute, scheduledTimeInMs, intervalInMs, _timer.ElapsedMilliseconds);
            QueueEvent(pending);
            return pending;
        }

        public void QueueEvent(IPendingEvent pending)
        {
            lock (_lock)
            {
                AddPending(pending);
                OnTimeCheck(null, false);
            }
        }

        private void AddPending(IPendingEvent pending)
        {
            List<IPendingEvent> list = null;
            if (!_pending.TryGetValue(pending.Expiration, out list))
            {
                list = new List<IPendingEvent>(2);
                _pending[pending.Expiration] = list;
            }
            list.Add(pending);
        }

        private bool SetTimer()
        {
            if (_cancel != null)
            {
                _cancel.Unregister(_waiter);
            }
            if (_pending.Count > 0)
            {
                long timeInMs = 0;
                if (GetTimeTilNext(ref timeInMs, _timer.ElapsedMilliseconds))
                {
                    _cancel = ThreadPool.RegisterWaitForSingleObject(_waiter, OnTimeCheck, null,
                        (uint)timeInMs, true);
                    return true;
                }
                return false;
            }
            else
            {
                return true;
            }
        }

        void OnTimeCheck(object sender, bool e)
        {
            if (!_running)
                return;
            lock (_lock)
            {
                do
                {
                    List<IPendingEvent> rescheduled = ExecuteExpired();
                    Queue(rescheduled);
                } while (!SetTimer());
            }
        }

        private void Queue(List<IPendingEvent> rescheduled)
        {
            if (rescheduled != null)
            {
                foreach (IPendingEvent pendingEvent in rescheduled)
                {
                    QueueEvent(pendingEvent);
                }
            }
        }

        private List<IPendingEvent> ExecuteExpired()
        {
            SortedList<long, List<IPendingEvent>> expired = RemoveExpired();
            List<IPendingEvent> rescheduled = null;
            if (expired.Count > 0)
            {
                foreach (KeyValuePair<long, List<IPendingEvent>> pair in expired)
                {
                    foreach (IPendingEvent pendingEvent in pair.Value)
                    {
                        IPendingEvent next = pendingEvent.Execute(_timer.ElapsedMilliseconds);
                        if (next != null)
                        {
                            if (rescheduled == null)
                            {
                                rescheduled = new List<IPendingEvent>(1);
                            }
                            rescheduled.Add(next);
                        }
                    }
                }
            }
            return rescheduled;
        }

        private SortedList<long, List<IPendingEvent>> RemoveExpired()
        {
            lock (_lock)
            {
                SortedList<long, List<IPendingEvent>> expired = new SortedList<long, List<IPendingEvent>>();
                foreach (KeyValuePair<long, List<IPendingEvent>> pair in _pending)
                {
                    if (_timer.ElapsedMilliseconds >= pair.Key)
                    {
                        expired.Add(pair.Key, pair.Value);
                    }
                    else
                    {
                        break;
                    }
                }
                foreach (KeyValuePair<long, List<IPendingEvent>> pair in expired)
                {
                    _pending.Remove(pair.Key);
                }
                return expired;
            }
        }

        public bool GetTimeTilNext(ref long time, long now)
        {
            time = 0;
            if (_pending.Count > 0)
            {
                foreach (KeyValuePair<long, List<IPendingEvent>> pair in _pending)
                {
                    if(now >= pair.Key)
                    {
                        return false;
                    }
                    time = (pair.Key - now);
                    return true;
                }
            }
            return false;
        }

        public void Stop()
        {
            _running = false;
            _timer.Stop();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}