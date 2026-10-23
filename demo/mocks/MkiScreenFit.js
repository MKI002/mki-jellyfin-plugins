/* Example data for the MKI Screen Fit demo: a household's devices with the screens they reported, and recent decisions. */
(function () {
  'use strict';
  var ID = '10b53a4f-258a-459a-962b-2e29418e0775', H = 36e5, DAY = 864e5;
  function ago(ms) { return new Date(Date.now() - ms).toISOString(); }
  MkiDemo.plugin(ID, { EnableFullHdVersion: true, EnableTranscodeLimit: true, DeviceRules: [{ DeviceId: 'demo-firetv', Mode: 'FullHd' }] });

  function scr(w, h, at, reliable) { return { Width: w, Height: h, Reliable: reliable !== false, Current: true, At: ago(at), FullHd: Math.min(w, h) <= 1200 }; }
  var devices = [
    { Id: 'demo-laptop', Name: 'Firefox', App: 'Jellyfin Web', AppVersion: '12.2.0', Users: ['Alex'], LastActive: ago(0.4 * H), Screen: scr(1920, 1080, 0.4 * H) },
    { Id: 'demo-firetv', Name: 'Bedroom Fire TV', App: 'Jellyfin Android TV', AppVersion: '0.19.10', Users: ['Sam'], LastActive: ago(3 * H), Screen: null },
    { Id: 'demo-desktop', Name: 'Edge Chromium', App: 'Jellyfin Web', AppVersion: '12.2.0', Users: ['Robin'], LastActive: ago(5 * H), Screen: scr(3840, 2160, 5 * H) },
    { Id: 'demo-phone', Name: 'Pixel 9', App: 'Jellyfin Android', AppVersion: '2.7.3', Users: ['Kim'], LastActive: ago(9 * H), Screen: scr(2424, 1080, 9 * H) },
    { Id: 'demo-tv', Name: 'Living room TV', App: 'Jellyfin Android TV', AppVersion: '0.19.10', Users: ['Alex', 'Sam'], LastActive: ago(1.2 * DAY), Screen: null },
    { Id: 'demo-ipad', Name: 'iPad', App: 'Jellyfin iPadOS', AppVersion: '1.8.0', Users: ['Jordan'], LastActive: ago(2 * DAY), Screen: scr(2360, 1640, 2 * DAY) },
    { Id: 'demo-webos', Name: 'LG webOS TV', App: 'Jellyfin Web', AppVersion: '12.2.0', Users: ['Sam'], LastActive: ago(4 * DAY), Screen: scr(1920, 1080, 4 * DAY, false) },
    { Id: 'demo-jmp', Name: 'Study PC', App: 'Jellyfin Media Player', AppVersion: '1.12.0', Users: ['Robin'], LastActive: ago(8 * DAY), Screen: null },
    { Id: 'demo-old', Name: 'Chrome', App: 'Jellyfin Web', AppVersion: '12.0.0', Users: ['Guest'], LastActive: ago(140 * DAY), Screen: null }
  ];
  var activity = [
    { At: ago(0.4 * H), Kind: 'version', DeviceName: 'Firefox', UserName: 'Alex', ItemName: 'Blade Runner 2049', From: '3840x2160', To: '1920x1080', Reason: '', Screen: '1920x1080' },
    { At: ago(0.4 * H), Kind: 'limit', DeviceName: 'Firefox', UserName: 'Alex', ItemName: 'Blade Runner 2049', From: '1920x1080', To: 'at most 1920x1080', Reason: 'VideoRangeTypeNotSupported', Screen: '1920x1080' },
    { At: ago(3 * H), Kind: 'version', DeviceName: 'Bedroom Fire TV', UserName: 'Sam', ItemName: 'Dune: Part Two', From: '3840x1600', To: '1920x800', Reason: '', Screen: 'set to Full HD' },
    { At: ago(9 * H), Kind: 'kept', DeviceName: 'Pixel 9', UserName: 'Kim', ItemName: 'Oppenheimer', From: '3840x2160', To: '', Reason: 'no Full HD version', Screen: '2424x1080' },
    { At: ago(9 * H), Kind: 'limit', DeviceName: 'Pixel 9', UserName: 'Kim', ItemName: 'Oppenheimer', From: '3840x2160', To: 'at most 2424x1080', Reason: 'VideoCodecNotSupported', Screen: '2424x1080' },
    { At: ago(30 * H), Kind: 'kept', DeviceName: 'Firefox', UserName: 'Alex', ItemName: 'The Lord of the Rings', From: '3840x2160', To: '', Reason: 'the Full HD version has a different length (another cut?)', Screen: '1920x1080' }
  ];

  MkiDemo.route('GET', /^MkiScreenFit\/Overview$/, function () {
    return {
      Devices: devices, Activity: activity, Versions: 2, Limits: 2, Kept: 2, Since: ago(2 * DAY),
      FullHdMaxShortSide: 1200, MinLimit: { Width: 1280, Height: 720 }
    };
  });
})();
