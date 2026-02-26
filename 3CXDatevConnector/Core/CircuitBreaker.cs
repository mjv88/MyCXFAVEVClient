using System;
using DatevConnector.Datev.Managers;

namespace DatevConnector.Core
{
    public enum CircuitState
    {
        Closed,
        Open,
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
        private DateTime _lastStateChange;

        public CircuitBreaker(string name, int failureThreshold = 3, int openTimeoutSeconds = 30, int halfOpenTestTimeoutSeconds = 5)
        {
            _name = name;
            _failureThreshold = failureThreshold;
            _openTimeout = TimeSpan.FromSeconds(openTimeoutSeconds);
            _halfOpenTestTimeout = TimeSpan.FromSeconds(halfOpenTestTimeoutSeconds);
            _state = CircuitState.Closed;
            _failureCount = 0;
            _lastStateChange = DateTime.UtcNow;
        }

        public bool IsOperationAllowed()
        {
            lock (_lock)
            {
                switch (_state)
                {
                    case CircuitState.Closed:
                        return true;

                    case CircuitState.Open:
                        if (DateTime.UtcNow - _lastStateChange >= _openTimeout)
                        {
                            TransitionTo(CircuitState.HalfOpen);
                            return true;
                        }
                        return false;

                    case CircuitState.HalfOpen:
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

        public void RecordSuccess()
        {
            lock (_lock)
            {
                _failureCount = 0;

                if (_state == CircuitState.HalfOpen)
                {
                    TransitionTo(CircuitState.Closed);
                }
            }
        }

        public void RecordFailure()
        {
            lock (_lock)
            {
                _failureCount++;

                if (_state == CircuitState.HalfOpen)
                {
                    TransitionTo(CircuitState.Open);
                }
                else if (_state == CircuitState.Closed && _failureCount >= _failureThreshold)
                {
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
