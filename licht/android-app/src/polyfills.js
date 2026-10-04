// JavaScript features matter.js uses that Hermes (React Native's engine) does not have (yet).
// Must run before matter.js is loaded.

// Used for `using` / `await using` (explicit resource management).
if (typeof Symbol.dispose !== 'symbol') {
  Object.defineProperty(Symbol, 'dispose', { value: Symbol.for('Symbol.dispose') });
}
if (typeof Symbol.asyncDispose !== 'symbol') {
  Object.defineProperty(Symbol, 'asyncDispose', { value: Symbol.for('Symbol.asyncDispose') });
}

// Only used to clean up after garbage collection – doing nothing is fine.
if (typeof globalThis.FinalizationRegistry !== 'function') {
  globalThis.FinalizationRegistry = class FinalizationRegistry {
    register() {}
    unregister() {
      return false;
    }
  };
}

if (typeof globalThis.AggregateError !== 'function') {
  globalThis.AggregateError = class AggregateError extends Error {
    constructor(errors, message) {
      super(message);
      this.name = 'AggregateError';
      this.errors = [...errors];
    }
  };
}

if (typeof globalThis.SuppressedError !== 'function') {
  globalThis.SuppressedError = class SuppressedError extends Error {
    constructor(error, suppressed, message) {
      super(message);
      this.name = 'SuppressedError';
      this.error = error;
      this.suppressed = suppressed;
    }
  };
}

if (typeof globalThis.AbortSignal === 'function') {
  if (typeof AbortSignal.timeout !== 'function') {
    AbortSignal.timeout = (ms) => {
      const controller = new AbortController();
      setTimeout(() => controller.abort(new Error('TimeoutError')), ms);
      return controller.signal;
    };
  }
  if (typeof AbortSignal.any !== 'function') {
    AbortSignal.any = (signals) => {
      const controller = new AbortController();
      for (const signal of signals) {
        if (signal.aborted) {
          controller.abort(signal.reason);
          break;
        }
        signal.addEventListener('abort', () => controller.abort(signal.reason), { once: true });
      }
      return controller.signal;
    };
  }
}

// Used to recognize aborted operations ("AbortError").
if (typeof globalThis.DOMException !== 'function') {
  globalThis.DOMException = class DOMException extends Error {
    constructor(message = '', name = 'Error') {
      super(message);
      this.name = name;
    }
  };
}
