// A simulated Matter colour lamp for tests. Prints one JSON line per event to stdout.
import './no-ipv6.cjs';
import { Environment, Logger, ServerNode, VendorId } from '@matter/main';
import { ExtendedColorLightDevice } from '@matter/main/devices/extended-color-light';
import { ColorControlServer } from '@matter/main/behaviors/color-control';
import { LevelControlServer } from '@matter/main/behaviors/level-control';

Logger.level = 'error';
const env = Environment.default;
env.vars.set('storage.path', process.env.STORE);
const port = Number(process.env.PORT ?? 5540);
const out = (data) => process.stdout.write(`${JSON.stringify(data)}\n`);

const node = await ServerNode.create({
  id: `virtual-light-${port}`,
  network: { port },
  commissioning: { passcode: 20202021, discriminator: 3840 },
  productDescription: { name: 'Virtual A60', deviceType: ExtendedColorLightDevice.deviceType },
  basicInformation: {
    vendorName: 'Test', productName: 'Virtuelle A60', vendorId: VendorId(0xfff1), productId: 0x8000,
    serialNumber: `virt-${port}`, nodeLabel: '',
  },
});
const light = await node.add(
  ExtendedColorLightDevice.with(
    LevelControlServer.with('Lighting', 'OnOff'),
    ColorControlServer.with('HueSaturation', 'Xy', 'ColorTemperature'),
  ),
  {
    id: 'light',
    levelControl: { currentLevel: 254 },
    colorControl: {
      colorTempPhysicalMinMireds: 153, colorTempPhysicalMaxMireds: 370, coupleColorTempToLevelMinMireds: 153,
      startUpColorTemperatureMireds: null, colorTemperatureMireds: 300,
      colorMode: 2, enhancedColorMode: 2, currentHue: 0, currentSaturation: 0, currentX: 24939, currentY: 24701,
    },
  },
);
for (const [cluster, attr] of [['onOff', 'onOff'], ['levelControl', 'currentLevel'], ['colorControl', 'colorMode'],
  ['colorControl', 'currentHue'], ['colorControl', 'currentSaturation'], ['colorControl', 'colorTemperatureMireds']]) {
  light.events[cluster][`${attr}$Changed`].on((value) => out({ attr, value }));
}
await node.start();
out({ ready: true, code: node.state.commissioning.pairingCodes.manualPairingCode });
