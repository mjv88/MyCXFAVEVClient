using System;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    /// <summary>
    /// Circuit breaker states
    /// </summary>
    public enum CircuitState
    {
        /// <summary>Circuit is closed - operations proceed normally</summary>
        Closed,
        /// <summary>Circuit is open - operations are blocked</summary>
        Open,
        /// <summary>Circuit is half-open - testing if operations can succeed</summary>
        HalfOpen
    }

    /// <summary>
    /// Circuit breaker pattern implementation to prevent cascading failures.
    /// When failures exceed threshold, the circuit opens and blocks further attempts
    /// for a configured timeout period, then allows a test request through.
    /// </summary>
    public class CircuitBreaker
    {
        private readonly object _lock = new object();
        private readonly string _name;
        private readonly int _failureThreshold;
        private readonly TimeSpan _openTimeout;
        private readonly TimeSpan _halfOpenTestTimeout;

        private CircuitState _state;
        private int _failureCount;
        private DateTime _lastFailureTime;
        private DateTime _lastStateChange;

        /// <summary>
        /// Creates a new circuit breaker
        /// </summary>
        /// <param name="name">Name for logging</param>
        /// <param name="failureThreshold">Number of failures before opening circuit</param>
        /// <param name="openTimeoutSeconds">Seconds to wait before allowing test request</param>
        /// <param name="halfOpenTestTimeoutSeconds">Seconds between test requests in half-open state</param>
        public CircuitBreaker(string name, int failureThreshold = 3, int openTimeoutSeconds = 30, int halfOpenTestTimeoutSeconds = 5)
        {
            _name = name;
            _failureThreshold = failureThreshold;
            _openTimeout = TimeSpan.FromSeconds(openTimeoutSeconds);
            _halfOpenTestTimeout = TimeSpan.FromSeconds(halfOpenTestTimeoutSeconds);
            _state = CircuitState.Closed;
            _failureCount = 0;
            _lastFailureTime = DateTime.MinValue;
            _lastStateChange = DateTime.UtcNow;
        }

        /// <summary>
        /// Checks if an operation is allowed to proceed
        /// </summary>
        /// <returns>True if operation can proceed, false if circuit is open</returns>
        public bool IsOperationAllowed()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return true;

                    case CircuitState.Open:
                        // Check if timeout has elapsed
                        if (DateTime.UtcNow - _lastStateChange >= _openTimeout)
                        {
                            // Transition to half-open to allow a test request
                            TransitionTo(CircuitState.HalfOpen);
                            return true;
                        }
                        return false;

                    case CircuitState.HalfOpen:
                        // In half-open, only allow one test request at a time
                        // If enough time has passed since last state change, allow test
                        if (DateTime.UtcNow - _lastStateChange >= _halfOpenTestTimeout)
                        {
                            _lastStateChange = DateTime.UtcNow;
                            return true;
                        }
                        return false;

                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Records a successful operation
        /// </summary>
        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failureCount = 0;

                if (_state == CircuitState.HalfOpen)
                {
                    // Test succeeded, close the circuit
                    TransitionTo(CircuitState.Closed);
                }
            }
        }

        /// <summary>
        /// Records a failed operation
        /// </summary>
        public void RecordFailure()
        {
            lock (_lock)
            {
                _failureCount++;
                _lastFailureTime = DateTime.UtcNow;

                if (_state == CircuitState.HalfOpen)
                {
                    // Test failed, reopen the circuit
                    TransitionTo(CircuitState.Open);
                }
                else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
                {
                    // Too many failures, open the circuit
                    TransitionTo(CircuitState.Open);
                }
            }
        }

        private void TransitionTo(CircuitState newState)
        {
            if (_state != newState)
            {
                CircuitState oldState = _state;
                _state = newState;
                _lastStateChange = DateTime.UtcNow;

                if (newState == CircuitState.Open)
                    LogManager.Log("[{0}] Circuit: {1} -> {2} (failures={3}/{4}, retry in {5}s)",
                        _name, oldState, newState, _failureCount, _failureThreshold, _openTimeout.TotalSeconds);
                else
                    LogManager.Log("[{0}] Circuit: {1} -> {2}", _name, oldState, newState);
            }
        }
    }
}
