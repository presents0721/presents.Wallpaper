/* Alife 动态壁纸 —— 渲染端
 * 通过 Electron IPC 接收 C# 下发的 { type:"wallpaper", kind, url, preview, fit, mute, volume }
 * 支持：视频 <video> / 网页 <iframe> / 场景预览图 <img>
 */
(function () {
  'use strict';

  var channel = (function () {
    var m = /[?&]channel=([^&]+)/.exec(location.search);
    return m ? decodeURIComponent(m[1]) : null;
  })();

  var ipc = null;
  try {
    if (window.require) {
      var electron = window.require('electron');
      if (electron && electron.ipcRenderer) ipc = electron.ipcRenderer;
    }
  } catch (e) { ipc = null; }

  var video = document.getElementById('video');
  var web = document.getElementById('web');
  var image = document.getElementById('image');
  var hint = document.getElementById('hint');

  function send(type, payload) {
    try {
      if (!ipc || !channel) return;
      var msg = { type: type };
      if (payload) { for (var k in payload) msg[k] = payload[k]; }
      ipc.send(channel, JSON.stringify(msg));
    } catch (e) { /* 通道不可用时只在控制台 */ }
  }

  function log(level, text) {
    send('log', { level: level, text: String(text) });
  }

  window.addEventListener('error', function (ev) {
    log('error', 'window.onerror: ' + (ev && ev.message) + ' @' + (ev && ev.filename) + ':' + (ev && ev.lineno));
  });
  window.addEventListener('unhandledrejection', function (ev) {
    log('error', 'unhandledrejection: ' + (ev && ev.reason && (ev.reason.message || ev.reason)));
  });
  ['error', 'warn'].forEach(function (level) {
    var original = console[level];
    if (typeof original !== 'function') return;
    console[level] = function () {
      try {
        var parts = [];
        for (var i = 0; i < arguments.length; i++) {
          var a = arguments[i];
          parts.push(a instanceof Error ? (a.message + '\n' + a.stack)
            : (typeof a === 'object' ? JSON.stringify(a) : String(a)));
        }
        log(level, parts.join(' '));
      } catch (e) { /* ignore */ }
      return original.apply(console, arguments);
    };
  });

  function hideAll() {
    try { video.pause(); } catch (e) { }
    video.removeAttribute('src');
    try { video.load(); } catch (e) { }
    web.removeAttribute('src');
    image.removeAttribute('src');
    [video, web, image].forEach(function (el) {
      el.style.opacity = '0';
      el.style.display = 'none';
    });
    hint.style.display = 'none';
  }

  // 先透明显示再下一帧拉到不透明，形成淡入
  function showElement(el) {
    el.style.opacity = '0';
    el.style.display = 'block';
    requestAnimationFrame(function () { el.style.opacity = '1'; });
  }

  function apply(msg) {
    if (!msg || !msg.type) return;

    if (msg.type === 'clear') {
      hideAll();
      return;
    }

    if (msg.type !== 'wallpaper') return;

    hideAll();
    var fit = msg.fit === 'contain' ? 'contain' : 'cover';

    if (msg.kind === 'video' && msg.url) {
      video.style.objectFit = fit;
      showElement(video);
      video.muted = !!msg.mute;
      video.volume = Math.max(0, Math.min(1, (msg.volume | 0) / 100));
      video.src = msg.url;

      var playing = video.play();
      if (playing && playing.catch) {
        playing.catch(function (e) { log('warn', 'video play failed: ' + (e && e.message || e)); });
      }
      return;
    }

    if (msg.kind === 'web' && msg.url) {
      showElement(web);
      web.src = msg.url;
      return;
    }

    if (msg.preview) {
      image.style.objectFit = fit;
      showElement(image);
      image.src = msg.preview;
      hint.textContent = '场景壁纸暂以预览图显示（实时 WebGL 渲染待接入）';
      hint.style.display = 'block';
      return;
    }

    hint.textContent = '该壁纸没有可显示的预览图';
    hint.style.display = 'block';
  }

  if (ipc && channel) {
    ipc.on(channel, function (ev, json) {
      try { apply(JSON.parse(json)); }
      catch (e) { log('warn', 'bad message: ' + (e && e.message || e)); }
    });
    ipc.send(channel, JSON.stringify({ type: 'init' }));
  } else {
    log('warn', 'ipcRenderer 不可用，壁纸无法接收指令');
  }
})();
