/***************************************************************************** 
* Copyright 2016 Aurora Solutions 
* 
*    http://www.aurorasolutions.io 
* 
* Aurora Solutions is an innovative services and product company at 
* the forefront of the software industry, with processes and practices 
* involving Domain Driven Design(DDD), Agile methodologies to build 
* scalable, secure, reliable and high performance products.
* 
* TradeSharp is a C# based data feed and broker neutral Algorithmic 
* Trading Platform that lets trading firms or individuals automate 
* any rules based trading strategies in stocks, forex and ETFs. 
* TradeSharp allows users to connect to providers like Tradier Brokerage, 
* IQFeed, FXCM, Blackwood, Forexware, Integral, HotSpot, Currenex, 
* Interactive Brokers and more. 
* Key features: Place and Manage Orders, Risk Management, 
* Generate Customized Reports etc 
* 
* Licensed under the Apache License, Version 2.0 (the "License"); 
* you may not use this file except in compliance with the License. 
* You may obtain a copy of the License at 
* 
*    http://www.apache.org/licenses/LICENSE-2.0 
* 
* Unless required by applicable law or agreed to in writing, software 
* distributed under the License is distributed on an "AS IS" BASIS, 
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. 
* See the License for the specific language governing permissions and 
* limitations under the License. 
*****************************************************************************/


﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Disruptor;
using Spring.Context.Support;
using TraceSourceLogger;
using TradeHub.Common.Core.Constants;
using TradeHub.Common.Core.DomainModels;
using TradeHub.Common.Core.DomainModels.OrderDomain;
using TradeHub.Common.HistoricalDataProvider.Service;
using TradeHub.Common.HistoricalDataProvider.Utility;
using TradeHub.Common.HistoricalDataProvider.ValueObjects;
using TradeHub.Common.Persistence;
using TradeHub.StrategyEngine.TradeHub;
using TradeHub.StrategyEngine.Utlility.Services;
using TradeHubGui.Common;
using TradeHubGui.Common.Models;
using TradeHubGui.StrategyRunner.Representations;
using OrderExecutionProvider = TradeHub.Common.Core.Constants.OrderExecutionProvider;
using Strategy = TradeHub.Common.Core.DomainModels.Strategy;
using TradeHubConstants = TradeHub.Common.Core.Constants;

namespace TradeHubGui.StrategyRunner.Executors
{
    /// <summary>
    /// Responsibe for handling individual strategy instances
    /// </summary>
    internal class StrategyExecutor : IDisposable
    {
        private Type _type = typeof(StrategyExecutor);

        private AsyncClassLogger _asyncClassLogger;

        /// <summary>
        /// Holds UI thread reference
        /// </summary>
        private Dispatcher _currentDispatcher;

        /// <summary>
        /// Holds necessary information for Instance Execution and UI-Update
        /// </summary>
        private readonly StrategyInstance _strategyInstance;

        /// <summary>
        /// Responsible for providing order executions in backtesting
        /// </summary>
        private IOrderExecutor _orderExecutor;

        /// <summary>
        /// Manages order requests from strategy in backtesting
        /// </summary>
        private OrderRequestListener _orderRequestListener;

        /// <summary>
        /// Manages market data for backtesting strategy
        /// </summary>
        private MarketDataListener _marketDataListener;

        /// <summary>
        /// Manages market data requests from strategy in backtesting
        /// </summary>
        private MarketRequestListener _marketRequestListener;

        /// <summary>
        /// Responsible for providing requested data
        /// </summary>
        private DataHandler _dataHandler;

        /// <summary>
        /// Unique Key to Identify the Strategy Instance
        /// </summary>
        private string _strategyKey;

        /// <summary>
        /// Save Custom Strategy Type (C# Class Type which implements TradeHubStrategy.cs)
        /// </summary>
        private Type _strategyType;

        /// <summary>
        /// Holds reference of Strategy Instance
        /// </summary>
        private TradeHubStrategy _tradeHubStrategy;

        /// <summary>
        /// Holds selected ctor arguments to execute strategy
        /// </summary>
        private object[] _ctorArguments;

        /// <summary>
        /// Indicates if the Strategy Instance was requested to Stop Execution by the user
        /// </summary>
        private bool _stopInstanceRequested;

        private bool _disposed = false;

        #region Notification Fields

        /// <summary>
        /// Indicates if notification for new order is to be sent
        /// </summary>
        private bool _newOrderNotification = false;

        /// <summary>
        /// Indicates if notification on order acceptance is to be sent
        /// </summary>
        private bool _acceptedOrderNotification = false;

        /// <summary>
        /// Indicates if notification on order execution is to be sent
        /// </summary>
        private bool _executionNotification = false;

        /// <summary>
        /// Indicates if the notification on order rejection is to be sent
        /// </summary>
        private bool _rejectionNotification = false;

        #endregion

        #region Events

        // ReSharper disable InconsistentNaming
        private event Action<string, StrategyStatus> _statusChanged;
        private event Action<ExecutionRepresentation> _executionReceived;
        // ReSharper restore InconsistentNaming

        /// <summary>
        /// Raised when custom strategy status changed from Running-to-Stopped and vice versa
        /// </summary>
        public event Action<string, StrategyStatus> StatusChanged
        {
            add { if (_statusChanged == null) _statusChanged += value; }
            remove { _statusChanged -= value; }
        }

        /// <summary>
        /// Raised when new execution is received by the custom strategy
        /// </summary>
        public event Action<ExecutionRepresentation> ExecutionReceived
        {
            add { if (_executionReceived == null) _executionReceived += value; }
            remove { _executionReceived -= value; }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Unique Key to Identify the Strategy Instance
        /// </summary>
        public string StrategyKey
        {
            get { return _strategyKey; }
            set { _strategyKey = value; }
        }

        /// <summary>
        /// Holds selected ctor arguments to execute strategy
        /// </summary>
        public object[] CtorArguments
        {
            get { return _ctorArguments; }
            set { _ctorArguments = value; }
        }

        /// <summary>
        /// Indicates whether the strategy is None/Executing/Executed
        /// </summary>
        public StrategyStatus StrategyStatus
        {
            get { return _strategyInstance.Status; }
            set
            {
                _strategyInstance.Status = value;
            }
        }

        /// <summary>
        /// Indicates if notification for new order is to be sent
        /// </summary>
        public bool NewOrderNotification
        {
            get { return _newOrderNotification; }
        }

        /// <summary>
        /// Indicates if notification on order acceptance is to be sent
        /// </summary>
        public bool AcceptedOrderNotification
        {
            get { return _acceptedOrderNotification; }
        }

        /// <summary>
        /// Indicates if notification on order execution is to be sent
        /// </summary>
        public bool ExecutionNotification
        {
            get { return _executionNotification; }
        }

        /// <summary>
        /// Indicates if the notification on order rejection is to be sent
        /// </summary>
        public bool RejectionNotification
        {
            get { return _rejectionNotification; }
        }

        #endregion

        /// <summary>
        /// Argument Constructor
        /// </summary>
        /// <param name="strategyInstance">Holds necessary information for Instance Execution and UI-Update</param>
        /// <param name="currentDispatcher"></param>
        public StrategyExecutor(StrategyInstance strategyInstance, Dispatcher currentDispatcher)
        {
            this._currentDispatcher = currentDispatcher;

            _asyncClassLogger = new AsyncClassLogger("StrategyExecutor");

            //set logging path
            string path = DirectoryStructure.CLIENT_LOGS_LOCATION;

            _asyncClassLogger.SetLoggingLevel();
            _asyncClassLogger.LogDirectory(path);

            _tradeHubStrategy = null;
            _strategyInstance = strategyInstance;
            _strategyKey = _strategyInstance.InstanceKey;
            _strategyType = _strategyInstance.StrategyType;
            _ctorArguments = _strategyInstance.GetParameterValues();
        }

        /// <summary>
        /// Starts custom strategy execution
        /// </summary>
        public void ExecuteStrategy()
        {
            try
            {
                bool parameterChanged = true;

                // Check if any strategy parameter was changed
                if (_ctorArguments != null)
                {
                    parameterChanged = _strategyInstance.ParametersChanged(_ctorArguments);

                    // Get updated values to be used
                    if (parameterChanged)
                    {
                        // Get parameter values to be used
                        _ctorArguments = _strategyInstance.GetParameterValues();
                    }
                }

                // Verify Strategy Instance
                if (_tradeHubStrategy == null || parameterChanged)
                {
                    //create DB strategy 
                    Strategy strategy = new Strategy();
                    strategy.Name = _strategyType.Name;
                    strategy.StartDateTime = DateTime.Now;

                    // Get parameter values to be used
                    _ctorArguments = _strategyInstance.GetParameterValues();

                    // Get new strategy instance
                    var strategyInstance = StrategyHelper.CreateStrategyInstance(_strategyType, _ctorArguments);

                    if (strategyInstance != null)
                    {
                        // Cast to TradeHubStrategy Instance
                        _tradeHubStrategy = strategyInstance as TradeHubStrategy;
                    }

                    if (_tradeHubStrategy == null)
                    {
                        if (_asyncClassLogger.IsInfoEnabled)
                        {
                            _asyncClassLogger.Info("Unable to initialize Custom Strategy: " + _strategyType.FullName, _type.FullName, "ExecuteStrategy");
                        }

                        // Skip execution of further actions
                        return;
                    }

                    // Set Strategy Name
                    _tradeHubStrategy.StrategyName = StrategyHelper.GetCustomClassSummary(_strategyType);

                    // Set notificaitons preference
                    _tradeHubStrategy.SetNewOrderNotification(_newOrderNotification);
                    _tradeHubStrategy.SetAcceptedOrderNotification(_acceptedOrderNotification);
                    _tradeHubStrategy.SetExecutionNotification(_executionNotification);
                    _tradeHubStrategy.SetRejectionNotification(_rejectionNotification);

                    // Register Events
                    RegisterTradeHubStrategyEvent();
                }

                if (_asyncClassLogger.IsInfoEnabled)
                {
                    _asyncClassLogger.Info("Executing user strategy: " + _strategyType.FullName, _type.FullName, "ExecuteStrategy");
                }

                //Overriding if running on simulated exchange
                ManageBackTestingStrategy();

                _stopInstanceRequested = false;

                // Start Executing the strategy
                _tradeHubStrategy.Run();
            }
            catch (Exception exception)
            {
                _asyncClassLogger.Error(exception, _type.FullName, "ExecuteStrategy");
            }
        }

        /// <summary>
        /// Stops custom strategy execution
        /// </summary>
        public void StopStrategy()
        {
            try
            {
                // Verify Strategy Instance
                if (_tradeHubStrategy != null)
                {
                    if (_asyncClassLogger.IsInfoEnabled)
                    {
                        _asyncClassLogger.Info("Stopping user strategy execution: " + _strategyType.FullName, _type.FullName, "StopStrategy");
                    }

                    _stopInstanceRequested = true;

                    // Start Executing the strategy
                    _tradeHubStrategy.Stop();
                }
                else
                {
                    if (_asyncClassLogger.IsInfoEnabled)
                    {
                        _asyncClassLogger.Info("User strategy not initialized: " + _strategyType.FullName, _type.FullName, "StopStrategy");
                    }
                }
            }
            catch (Exception exception)
            {
                _asyncClassLogger.Error(exception, _type.FullName, "StopStrategy");
            }
        }

        /// <summary>
        /// Subscribe events from TradeHub Strategy
        /// </summary>
        private void RegisterTradeHubStrategyEvent()
        {
            _tradeHubStrategy.OnStrategyStatusChanged += OnStrategyStatusChanged;
            
            _tradeHubStrategy.OrderAcceptedEvent += OnOrderAccepted;
            _tradeHubStrategy.OnNewExecutionReceived += OnNewExecutionReceived;
            _tradeHubStrategy.CancellationArrivedEvent += OnCancellationReceived;
            _tradeHubStrategy.RejectionArrivedEvent += OnRejectionReceived;

            _tradeHubStrategy.DisplayMessageEvent += StrategyMessageArrived;
        }

        #region Manage Back-Testing Strategy (i.e. Provider = SimulatedExchange)

        /// <summary>
        /// Will take appropariate actions to handle a strategy intended to be back tested
        /// </summary>
        private void ManageBackTestingStrategy()
        {
            if (_tradeHubStrategy != null)
            {
                if (_tradeHubStrategy.MarketDataProviderNames != null && _tradeHubStrategy.MarketDataProviderNames[0].Equals(TradeHubConstants.MarketDataProvider.SimulatedExchange))
                {
                    InitializeBacktestingDataComponents();
                    OverrideStrategyDataEvents();
                }
                if (_tradeHubStrategy.OrderExecutionProviderNames != null && _tradeHubStrategy.OrderExecutionProviderNames[0].Equals(OrderExecutionProvider.SimulatedExchange))
                {
                    InitializeBacktestingOrderComponents();
                    OverrideStrategyOrderRequests();
                }
            }
        }

        /// <summary>
        /// Initializes necessary market data components required for backtesting
        /// </summary>
        private void InitializeBacktestingDataComponents()
        {
            // Initialze Utility Classes to manage backtesting with saved market data
            _marketDataListener = new MarketDataListener(_asyncClassLogger);

            // Use MarketDataListener.cs as Event Handler to get data from DataHandler.cs
            _dataHandler = new DataHandler(new IEventHandler<MarketDataObject>[] { _marketDataListener });

            _marketDataListener.BarSubscriptionList = _dataHandler.BarSubscriptionList;
            _marketDataListener.TickSubscriptionList = _dataHandler.TickSubscriptionList;

            // Initialize MarketRequestListener.cs to manage incoming market data requests from strategy
            _marketRequestListener = new MarketRequestListener(_dataHandler, _asyncClassLogger);

            //Register Market Data Listener Events
            RegisterMarketDataListenerEvents();
        }

        /// <summary>
        /// Initializes necessary order execution components required for backtesting
        /// </summary>
        private void InitializeBacktestingOrderComponents()
        {
            // Initialze Utility Classes to manage backtesting order executions
            _orderExecutor = ContextRegistry.GetContext()["OrderExecutor"] as IOrderExecutor;
            _orderRequestListener = new OrderRequestListener(_orderExecutor, _asyncClassLogger);

            //Register OrderExecutor Events
            RegisterOrderExecutorEvents();
        }

        /// <summary>
        /// Overrides required data events for backtesting strategy
        /// </summary>
        private void OverrideStrategyDataEvents()
        {
            //NOTE: LOCAL Data

            _tradeHubStrategy.OverrideTickSubscriptionRequest(_marketRequestListener.SubscribeTickData);
            _tradeHubStrategy.OverrideTickUnsubscriptionRequest(_marketRequestListener.UnsubscribeTickData);

            _tradeHubStrategy.OverrideBarSubscriptionRequest(_marketRequestListener.SubscribeLiveBars);
            _tradeHubStrategy.OverrideBarSubscriptionRequest(_marketRequestListener.SubscribeMultipleLiveBars);
            _tradeHubStrategy.OverriderBarUnsubscriptionRequest(_marketRequestListener.UnsubcribeLiveBars);
        }

        /// <summary>
        /// Overrides backtesting strategy's order requests to manage them inside strategy runner
        /// </summary>
        private void OverrideStrategyOrderRequests()
        {
            _tradeHubStrategy.OverrideMarketOrderRequest(_orderRequestListener.NewMarketOrderRequest);
            _tradeHubStrategy.OverrideLimitOrderRequest(_orderRequestListener.NewLimitOrderRequest);
            _tradeHubStrategy.OverrideCancelOrderRequest(_orderRequestListener.NewCancelOrderRequest);
        }

        /// <summary>
        /// Subscribes order events from <see cref="OrderExecutor"/>
        /// </summary>
        private void RegisterOrderExecutorEvents()
        {
            _orderExecutor.NewOrderArrived += OnOrderExecutorNewArrived;
            _orderExecutor.ExecutionArrived += OnOrderExecutorExecutionArrived;
            _orderExecutor.RejectionArrived += OnOrderExecutorRejectionArrived;
            _orderExecutor.CancellationArrived += OnOrderExecutorCancellationArrived;
        }

        /// <summary>
        /// Subscribes Tick and Bars events from <see cref="MarketDataListener"/>
        ///  </summary>
        private void RegisterMarketDataListenerEvents()
        {
            _marketDataListener.TickArrived += OnTickArrived;
            _marketDataListener.BarArrived += OnBarArrived;
        }

        /// <summary>
        /// Called when Cancellation received from <see cref="OrderExecutor"/>
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderExecutorCancellationArrived(Order order)
        {
            _tradeHubStrategy.CancellationArrived(order);
            PersistencePublisher.PublishDataForPersistence(order);
        }

        /// <summary>
        /// Called when Rejection received from <see cref="OrderExecutor"/>
        /// </summary>
        /// <param name="rejection"></param>
        private void OnOrderExecutorRejectionArrived(Rejection rejection)
        {
            _tradeHubStrategy.RejectionArrived(rejection);
        }

        /// <summary>
        /// Called when Executions received from <see cref="OrderExecutor"/>
        /// </summary>
        /// <param name="execution"></param>
        private void OnOrderExecutorExecutionArrived(Execution execution)
        {
            _tradeHubStrategy.ExecutionArrived(execution);
            PersistencePublisher.PublishDataForPersistence(execution.Fill);
            PersistencePublisher.PublishDataForPersistence(execution.Order);
        }

        /// <summary>
        /// Called when New order status received from <see cref="OrderExecutor"/>
        /// </summary>
        /// <param name="order"></param>
        private void OnOrderExecutorNewArrived(Order order)
        {
            _tradeHubStrategy.NewArrived(order);
            PersistencePublisher.PublishDataForPersistence(order);
        }

        /// <summary>
        /// Called when bar received from <see cref="MarketDataListener"/>
        /// </summary>
        /// <param name="bar"></param>
        private void OnBarArrived(Bar bar)
        {
            if (_asyncClassLogger.IsDebugEnabled)
            {
                _asyncClassLogger.Debug(bar.ToString(), _type.FullName, "OnBarArrived");
            }
            _orderExecutor.BarArrived(bar);
            _tradeHubStrategy.OnBarArrived(bar);
        }

        /// <summary>
        /// Called when tick received from <see cref="MarketDataListener"/>
        /// </summary>
        /// <param name="tick"></param>
        private void OnTickArrived(Tick tick)
        {
            if (_asyncClassLogger.IsDebugEnabled)
            {
                _asyncClassLogger.Debug(tick.ToString(), _type.FullName, "OnTickArrived");
            }

            _orderExecutor.TickArrived(tick);
            _tradeHubStrategy.OnTickArrived(tick);
        }

        #endregion

        #region Strategy Events

        /// <summary>
        /// Called when Custom Strategy Running status changes
        /// </summary>
        /// <param name="status">Indicate whether the strategy is running or nor</param>
        private void OnStrategyStatusChanged(bool status)
        {
            if (status)
            {
                _currentDispatcher.Invoke(DispatcherPriority.Background, (Action) (() =>
                {
                    _strategyInstance.Status = StrategyStatus.Executing;
                }));
            }
            else
            {
                if (_stopInstanceRequested)
                {
                    _currentDispatcher.Invoke(DispatcherPriority.Background, (Action) (() =>
                    {
                        _strategyInstance.Status = StrategyStatus.Stopped;
                    }));
                }
                else
                {
                    _currentDispatcher.Invoke(DispatcherPriority.Background, (Action) (() =>
                    {
                        _strategyInstance.Status = StrategyStatus.Executed;
                    }));
                }
            }

            if (_statusChanged != null)
            {
                _statusChanged(_strategyKey, _strategyInstance.Status);
            }
        }

        /// <summary>
        /// Called when Custom Strategy receives requested order accepted message
        /// </summary>
        /// <param name="order">Accepted Order details</param>
        private void OnOrderAccepted(Order order)
        {
            //OrderDetails orderDetails = new OrderDetails();
            //orderDetails.ID = order.OrderID;
            ////orderDetails.Price = order.;
            //orderDetails.Quantity = order.OrderSize;
            //orderDetails.Side = order.OrderSide;
            ////orderDetails.Type = order.;
            //orderDetails.Status = order.OrderStatus;

            //// Update UI
            //AddOrderDetails(orderDetails);

            PersistencePublisher.PublishDataForPersistence(order);
        }

        /// <summary>
        /// Called when Custom Strategy receives notification from Order Cancellation
        /// </summary>
        /// <param name="order">Contains cancelled order information</param>
        private void OnCancellationReceived(Order order)
        {
            OrderDetails orderDetails = new OrderDetails(order.OrderExecutionProvider);
            orderDetails.ID = order.OrderID;
            orderDetails.Security = order.Security;
            orderDetails.Quantity = order.OrderSize;
            orderDetails.Side = order.OrderSide;
            orderDetails.Time = order.OrderDateTime;
            orderDetails.Status = order.OrderStatus;

            // Update UI
            AddOrderDetails(orderDetails);

            // Handle Persistence
            PersistencePublisher.PublishDataForPersistence(order);
        }

        /// <summary>
        /// Called when Custom Strategy's order is rejected
        /// </summary>
        /// <param name="rejection">Contains rejection details</param>
        private void OnRejectionReceived(Rejection rejection)
        {
            OrderDetails orderDetails = new OrderDetails(rejection.OrderExecutionProvider);
            orderDetails.ID = rejection.OrderId;
            orderDetails.Security = rejection.Security;
            orderDetails.Status = OrderStatus.REJECTED;

            // Update UI
            AddOrderDetails(orderDetails);
        }

        /// <summary>
        /// Called when Custom Strategy receives new execution message
        /// </summary>
        /// <param name="execution">Contains Execution Info</param>
        private void OnNewExecutionReceived(Execution execution)
        {
            // Update Stats
            OrderDetails orderDetails = new OrderDetails(execution.OrderExecutionProvider);
            orderDetails.ID = execution.Fill.OrderId;
            orderDetails.Security = execution.Fill.Security;
            orderDetails.Price = execution.Fill.ExecutionPrice;
            orderDetails.Quantity = execution.Fill.ExecutionSize;
            orderDetails.Side = execution.Fill.ExecutionSide;
            orderDetails.Status = execution.Order.OrderStatus;
            orderDetails.Time = execution.Fill.ExecutionDateTime;

            // Update UI
            AddOrderDetails(orderDetails);

            PersistencePublisher.PublishDataForPersistence(execution.Fill);
            PersistencePublisher.PublishDataForPersistence(execution.Order);
        }

        #endregion

        /// <summary>
        /// Adds new 'Order Details' information in 'Execution Details' object for Strategy Instance
        /// </summary>
        /// <param name="orderDetails"></param>
        private void AddOrderDetails(OrderDetails orderDetails)
        {
            _currentDispatcher.Invoke(DispatcherPriority.Background, (Action) (() => _strategyInstance.AddOrderDetails(orderDetails)));
        }

        /// <summary>
        /// Adds new 'Order Details' information in 'Execution Details' object for Strategy Instance
        /// </summary>
        /// <param name="message"></param>
        private void StrategyMessageArrived(string message)
        {
            //_currentDispatcher.Invoke(DispatcherPriority.Background, (Action)(() => _strategyInstance.InstanceSummary.Add(message)));

            StrategyStatistics statistics = new StrategyStatistics(_strategyKey, DateTime.Now, message);
            _currentDispatcher.Invoke(DispatcherPriority.Background,
                (Action) (() => EventSystem.Publish<StrategyStatistics>(statistics)));
        }

        /// <summary>
        /// Returns locally saved data from strategy
        /// </summary>
        /// <returns></returns>
        public IReadOnlyList<string> GetLocalData()
        {
            return _tradeHubStrategy.GetLocalData();
        }

        #region Notifications

        /// <summary>
        /// Sets property to indicate that notification is to be sent on new order request
        /// </summary>
        /// <param name="sendNotification"></param>
        public void SetNewOrderNotification(bool sendNotification)
        {
            // Update local value
            _newOrderNotification = sendNotification;

            // Update strategy value
            if (_tradeHubStrategy != null)
            {
                _tradeHubStrategy.SetNewOrderNotification(sendNotification);
            }
        }

        /// <summary>
        /// Sets property to indicate that notification is to be sent on order acceptance
        /// </summary>
        /// <param name="sendNotification"></param>
        public void SetAcceptedOrderNotification(bool sendNotification)
        {
            // Update local value
            _acceptedOrderNotification = sendNotification;

            // Update strategy value
            if (_tradeHubStrategy != null)
            {
                _tradeHubStrategy.SetAcceptedOrderNotification(sendNotification);
            }
        }

        /// <summary>
        /// Sets property to indicate that notification is to be sent on order execution
        /// </summary>
        /// <param name="sendNotification"></param>
        public void SetExecutionNotification(bool sendNotification)
        {
            // Update local value
            _executionNotification = sendNotification;

            // Update strategy value
            if (_tradeHubStrategy != null)
            {
                _tradeHubStrategy.SetExecutionNotification(sendNotification);
            }
        }

        /// <summary>
        /// Sets property to indicate that notification is to be sent on order rejection
        /// </summary>
        /// <param name="sendNotification"></param>
        public void SetRejectionNotification(bool sendNotification)
        {
            // Update local value
            _rejectionNotification = sendNotification;

            // Update strategy value
            if (_tradeHubStrategy != null)
            {
                _tradeHubStrategy.SetRejectionNotification(sendNotification);
            }
        }

        #endregion

        /// <summary>
        /// Disposes strategy objects
        /// </summary>
        public void Close()
        {
            try
            {
                if (_tradeHubStrategy != null)
                {
                    if (_dataHandler != null)
                    {
                        _dataHandler.Dispose();
                    }

                    _tradeHubStrategy.Dispose();
                }
            }
            catch (Exception exception)
            {
                _asyncClassLogger.Error(exception, _type.FullName, "Close");
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            GC.Collect();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Close();
                }

                // Release unmanaged resources.
                _asyncClassLogger = null;
                _marketDataListener = null;
                _marketRequestListener = null;
                _dataHandler = null;
                _tradeHubStrategy = null;
                _currentDispatcher = null;
                _disposed = true;
            }
        }
    }
}