// Stand-in for react-native in the probe (Node). Only what matter.js' RN platform touches.
module.exports = {
  Platform: { OS: 'android', select: (o) => o.android ?? o.default },
};
