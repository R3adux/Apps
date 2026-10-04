class BleManager {
  onStateChange() { return { remove() {} }; }
  state() { return Promise.resolve('PoweredOff'); }
  startDeviceScan() {}
  stopDeviceScan() {}
  destroy() {}
}
module.exports = { BleManager, BleError: Error, BleErrorCode: {}, State: { PoweredOn: 'PoweredOn', PoweredOff: 'PoweredOff' } };
