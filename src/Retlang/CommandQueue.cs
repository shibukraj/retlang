using System;
using System.Collections.Generic;
using System.Threading;

namespace Retlang
{
    public delegate void OnCommand();

    public interface ICommandQueue
    {
        void Enqueue(OnCommand command);
    }

    public interface ICommandRunner: ICommandQueue
    {
        bool ExecuteNext();
        void Run();
        void Stop();
    }

    public class CommandQueue: ICommandRunner
    {
        private readonly object _lock = new object();
        private bool _running = true;

        private readonly Queue<OnCommand> _commands = new Queue<OnCommand>();


        public void Enqueue(OnCommand command)
        {
            lock (_lock)
            {
                _commands.Enqueue(command);
                Monitor.PulseAll(_lock);
            }
        }

        public OnCommand Dequeue()
        {
            lock (_lock)
            {
                while (_commands.Count == 0 && _running)
                {
                    Monitor.Wait(_lock);
                }
                if (!_running)
                {
                    return null;
                }
                return _commands.Dequeue();
            }
        }

        public bool ExecuteNext()
        {
            OnCommand comm = Dequeue();
            if (comm != null)
            {
                comm();
                return true;
            }
            return false;
        }

        public void Run()
        {
            while (ExecuteNext())
            {
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _running = false;
                Monitor.PulseAll(_lock);
            }
        }
    }
}