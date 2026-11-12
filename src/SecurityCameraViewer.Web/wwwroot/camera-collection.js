'use strict';

// Presentation uses collection positions. Internal channels remain stable so
// reordering cannot retarget playback requests, recordings or integrations.
const CameraCollection = (() => {
  function entries(settings, includeDisabled = true) {
    const sources = settings.sources?.length ? settings.sources :
      Array.from({ length: settings.channelCount }, (_, index) => ({
        channel: index + 1, name: settings.cameraNames?.[index] || `Camera ${index + 1}`
      }));
    return sources.map((source, index) => ({ source, number: index + 1 }))
      .filter(entry => includeDisabled || entry.source.enabled !== false);
  }

  function label(settings, channel, name) {
    const position = number(settings, channel);
    const cameraName = name || settings.sources?.find(source => source.channel === channel)?.name ||
      settings.cameraNames?.[channel - 1] || `Camera ${channel}`;
    return position === null ? cameraName : `${position}. ${cameraName}`;
  }

  function number(settings, channel) {
    if (!settings.sources?.length) return channel;
    const index = settings.sources.findIndex(source => source.channel === channel);
    return index < 0 ? null : index + 1;
  }

  function nextChannel(sources) {
    const used = new Set(sources.map(source => source.channel));
    let channel = 1;
    while (used.has(channel)) channel++;
    return channel;
  }

  return { entries, label, number, nextChannel };
})();
