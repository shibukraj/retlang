using System.Collections.Generic;

namespace Retlang
{
    public class ProcessBus : IProcessBus, ISubscriber
    {
        public event OnQueueFull QueueFullEvent;

        private ITransferEnvelopeFactory _envelopeFactory;
        private readonly IMessageBus _bus;
        private readonly IProcessQueue _processThread;
        private readonly SubscriberRegistry _subscribers;

        public ProcessBus(IMessageBus messageBus, IProcessQueue runner, ITransferEnvelopeFactory factory)
        {
            _bus = messageBus;
            _processThread = runner;
            _envelopeFactory = factory;
            _subscribers = new SubscriberRegistry();
        }

        public IProcessQueue ProcessQueue
        {
            get { return _processThread; }
        }

        public ITransferEnvelopeFactory TransferEnvelopeFactory
        {
            get { return _envelopeFactory; }
            set { _envelopeFactory = value; }
        }

        public void Start()
        {
            _bus.Subscribe(this);
            _processThread.Start();
        }

        public void Stop()
        {
            _processThread.Stop();
            _bus.Unsubscribe(this);
        }

        public ITimerControl Schedule(Command command, long intervalInMs)
        {
            return _processThread.Schedule(command, intervalInMs);
        }

        public ITimerControl ScheduleOnInterval(Command command, long firstIntervalInMs, long regularIntervalInMs)
        {
            return _processThread.ScheduleOnInterval(command, firstIntervalInMs, regularIntervalInMs);
        }

        public void Enqueue(Command command)
        {
            _processThread.Enqueue(command);
        }

        public void Publish(object topic, object msg, object replyToTopic)
        {
            Publish(_envelopeFactory.Create(topic, msg, replyToTopic));
        }

        public void Publish(ITransferEnvelope toPublish)
        {
            _bus.Publish(toPublish);
        }

        public void Publish(object topic, object msg)
        {
            Publish(topic, msg, null);
        }

        public IUnsubscriber SubscribeToKeyedBatch<K, V>(ITopicMatcher topic, ResolveKey<K, V> keyResolver,
                                                         On<IDictionary<K, IMessageEnvelope<V>>> target,
                                                         int minBatchIntervalInMs)
        {
            KeyedBatchSubscriber<K, V> batch =
                new KeyedBatchSubscriber<K, V>(keyResolver, target, this, minBatchIntervalInMs);
            return CreateSubscription<V>(topic, batch.ReceiveMessage);
        }

        public IUnsubscriber SubscribeToBatch<T>(ITopicMatcher topic, On<IList<IMessageEnvelope<T>>> msg,
                                                 int minBatchIntervalInMs)
        {
            BatchSubscriber<T> batch = new BatchSubscriber<T>(msg, this, minBatchIntervalInMs);
            return CreateSubscription<T>(topic, batch.ReceiveMessage);
        }

        public IUnsubscriber Subscribe<T>(ITopicMatcher topic, OnMessage<T> msg)
        {
            OnMessage<T> asyncReceive = CreateReceiveOnProcessThread(msg);
            return CreateSubscription(topic, asyncReceive);
        }

        private IUnsubscriber CreateSubscription<T>(ITopicMatcher topic, OnMessage<T> asyncReceive)
        {
            TopicSubscriber<T> subscriber = new TopicSubscriber<T>(topic, asyncReceive);
            AddSubscription(subscriber);
            return new Unsubscriber(subscriber, _subscribers);
        }

        private OnMessage<T> CreateReceiveOnProcessThread<T>(OnMessage<T> msg)
        {
            // message received on message bus thread, then executed on process thread.
            OnMessage<T> onMsgBusThread = delegate(IMessageHeader header, T data)
                                              {
                                                  Command toExecute = delegate { msg(header, data); };
                                                  try
                                                  {
                                                      Enqueue(toExecute);
                                                  }
                                                  catch (QueueFullException full)
                                                  {
                                                      OnQueueFull(full, header, data);
                                                  }
                                              };
            return onMsgBusThread;
        }

        private void OnQueueFull(QueueFullException full, IMessageHeader header, object data)
        {
            OnQueueFull onExc = QueueFullEvent;
            if (onExc != null)
            {
                onExc(full, header, data);
            }
        }

        private void AddSubscription(ISubscriber subscriber)
        {
            _subscribers.Subscribe(subscriber);
        }

        public object CreateUniqueTopic()
        {
            return new object();
        }

        public IRequestReply<T> SendRequest<T>(ITransferEnvelope env)
        {
            object requestTopic = env.Header.ReplyTo;
            TopicRequestReply<T> req = new TopicRequestReply<T>();
            TopicSubscriber<T> subscriber = new TopicSubscriber<T>(new TopicEquals(requestTopic), req.OnReply);
            AddSubscription(subscriber);
            req.Unsubscriber = new Unsubscriber(subscriber, _subscribers);
            _bus.Publish(env);
            return req;
        }


        public IRequestReply<T> SendRequest<T>(object topic, object msg)
        {
            return SendRequest<T>(_envelopeFactory.Create(topic, msg, CreateUniqueTopic()));
        }

        public void Receive(ITransferEnvelope envelope, ref bool consumed)
        {
            if (_subscribers.Publish(envelope))
            {
                consumed = true;
            }
        }
    }
}