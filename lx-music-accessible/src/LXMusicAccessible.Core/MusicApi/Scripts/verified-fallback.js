/**
 * @name 随版本固定备用音源
 * @version 1.0.0
 * @description LX Music Accessible 随版本交付并以 SHA-256 校验的备用解析源
 */
const API_URL = 'https://music-api.gdstudio.xyz/api.php';
const MUSIC_QUALITY = {
  tx: ['128k', '320k', 'flac'],
  wy: ['128k', '320k', 'flac'],
  kg: ['128k', '320k', 'flac'],
  kw: ['128k', '320k', 'flac'],
  mg: ['128k', '320k', 'flac']
};

lx.on(lx.EVENT_NAMES.request, function (request) {
  if (request.action !== 'musicUrl') return Promise.reject(new Error('unsupported action'));
  const music = request.info.musicInfo || {};
  const id = music.songmid || music.hash || music.id;
  const quality = request.info.type || '128k';
  const url = API_URL + '?types=url&source=' + encodeURIComponent(request.source)
    + '&id=' + encodeURIComponent(id) + '&br=' + encodeURIComponent(quality);
  return new Promise(function (resolve, reject) {
    lx.request(url, { method: 'GET', timeout: 15000 }, function (error, response, body) {
      if (error) return reject(error);
      const result = body && body.url ? body.url : null;
      if (!result) return reject(new Error('no playable URL'));
      resolve(result);
    });
  });
});
