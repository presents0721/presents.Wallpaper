using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace presents.Wallpaper;

/// <summary>
/// 往 Alife 主窗口（WebView2 + Blazor）里注入一层全屏壁纸。
/// 主窗口本身就是 Chromium，由 GlobalUI 组件通过 IJSRuntime 执行脚本操作 DOM：
///   #alife-wallpaper  z-index:0，位于 .app-container（z-index:1）之后；
///   同时把 .main-content 的背景改为透明，让壁纸透出来。
/// 不再创建独立窗口，也不碰桌面 WorkerW。
/// </summary>
internal sealed class AlifeBackground
{
    const string InitScript = """
        (function () {
          // 版本守卫：插件重载后页面里还挂着旧脚本，必须让新版覆盖它，
          // 否则一直跑老逻辑（旧版没有挂件、也没有磨砂面板）。
          var VER = '5.7.2';
          if (window.__alifeWpVer === VER && window.__alifeWallpaperShow) return 'ready';

          // 换版本时旧挂件的 DOM 和事件还挂在页面上（__alifeWallpaperWidget 见到就早退），
          // 不拆掉的话重载插件后调的永远是上一版的按钮
          var stale = !!window.__alifeWpVer && window.__alifeWpVer !== VER;
          window.__alifeWpVer = VER;

          if (stale) {
            var oldBox = document.getElementById('alife-wp-widget');
            if (oldBox && oldBox.parentNode) oldBox.parentNode.removeChild(oldBox);
            var oldCss = document.getElementById('alife-wp-widget-style');
            if (oldCss && oldCss.parentNode) oldCss.parentNode.removeChild(oldCss);
          }

          // 覆盖前先还原旧版留下的透明化，否则 __alifeWpBg 标记会让新版跳过这些面板
          try { if (window.__alifeWallpaperRestore) window.__alifeWallpaperRestore(); } catch (e) { }

          // 根据配置刷新样式表（遮罩 / 模糊都走这里，调参不用重建 <video>）
          function wpApplyStyle(cfg) {
            var style = document.getElementById('alife-wallpaper-style');
            if (!style) return;

            var o = cfg && cfg.opacity != null ? +cfg.opacity : 0.35;
            var b = cfg && cfg.blur != null ? +cfg.blur : 0;
            o = Math.max(0, Math.min(1, o));
            b = Math.max(0, Math.min(30, b));

            var br = cfg && cfg.brightness != null ? +cfg.brightness : 1;
            var ct = cfg && cfg.contrast != null ? +cfg.contrast : 1;
            var sa = cfg && cfg.saturate != null ? +cfg.saturate : 1;
            br = Math.max(0, Math.min(2, br));
            ct = Math.max(0, Math.min(2, ct));
            sa = Math.max(0, Math.min(2, sa));

            // 遮罩颜色可切换：黑色压暗 / 白色提亮柔化
            var scrim = (cfg && cfg.whiteScrim)
              ? 'rgba(255,255,255,' + o + ')'
              : 'rgba(0,0,0,' + o + ')';

            style.textContent =
              'html,body{background:transparent!important;}' +
              '#alife-wallpaper{position:fixed;inset:0;z-index:0;overflow:hidden;pointer-events:none;background:#000;' +
                'filter:brightness(' + br + ') contrast(' + ct + ') saturate(' + sa + ');' +
                'opacity:1;transition:opacity .35s ease;}' +
              '#alife-wallpaper>video,#alife-wallpaper>img,#alife-wallpaper>iframe{width:100%;height:100%;object-fit:cover;border:0;display:block;}' +
              '#alife-wallpaper>iframe{pointer-events:none;}' +
              '.app-container{position:relative;z-index:1;background:' + scrim + '!important;backdrop-filter:blur(' + b + 'px)!important;transition:background-color .18s ease!important;}' +
              '.main-content{background:transparent!important;}';
          }

          // ---------------- Scene 实时渲染（WebWallGL） ----------------
          // 场景包交给 WebWallGL 在 WebGL2 里跑；先摆 preview 图，首帧出来再淡入。
          // 加载失败 / 超时 / 渲染报错都只留预览图，不留白屏。
          function wpLoadLib(src) {
            // 库按版本号重新加载：插件重载时页面不会刷新，缓存住旧库的话
            // 改了 lib 也永远不生效（排查时踩过）
            if (window.__alifeWpLib && window.__alifeWpLibVer === VER) return Promise.resolve(window.__alifeWpLib);
            if (window.__alifeWpLibPromise && window.__alifeWpLibVer === VER) return window.__alifeWpLibPromise;

            window.__alifeWpLibVer = VER;

            window.__alifeWpLibPromise = new Promise(function (resolve, reject) {
              var s = document.createElement('script');
              s.src = src + (src.indexOf('?') >= 0 ? '&' : '?') + 'v=' + VER;
              s.async = true;
              s.onload = function () {
                var lib = window.WebWallGL || null;
                if (!lib) { reject(new Error('WebWallGL 没有挂到 window')); return; }
                window.__alifeWpLib = lib;
                resolve(lib);
              };
              s.onerror = function () { reject(new Error('加载 webwallgl 失败')); };
              document.head.appendChild(s);
            });

            return window.__alifeWpLibPromise;
          }

          function wpDisposeScene() {
            var inst = window.__alifeWpScene;
            window.__alifeWpScene = null;

            if (inst) {
              // 不释放 pkg 缓存的话，逐张切场景会把几十 MB 的包一直压在内存里
              try { inst.destroy({ releasePkgCache: true }); } catch (e) { }
            }

            var host = document.getElementById('alife-wp-scene');
            if (host && host.parentNode) host.parentNode.removeChild(host);
          }

          // 窗口隐藏时 rAF 完全不跑：此时挂载永远等不到首帧，15s 看门狗一超时就退回静态
          // 预览图，之后点什么都没反应。所以隐藏期间不挂，等回到前台再挂。
          function wpArmMountRetry() {
            if (window.__alifeWpMountRetry) return;
            window.__alifeWpMountRetry = true;

            document.addEventListener('visibilitychange', function () {
              if (document.hidden) return;

              window.__alifeWpMountRetry = false;

              var cfg = window.__alifeWpCfg || {};
              var root = document.getElementById('alife-wallpaper');

              if (!root || !cfg.liveScene || !cfg.pkg || !cfg.lib) return;
              if (document.getElementById('alife-wp-scene')) return;

              wpMountLiveScene(root, cfg);
            });
          }

          function wpMountLiveScene(root, cfg) {
            if (!window.__alifeWpVisHooked) {
              window.__alifeWpVisHooked = true;

              document.addEventListener('visibilitychange', function () {
                var inst = window.__alifeWpScene;

                if (document.hidden) {
                  if (inst) { try { inst.pause(); } catch (e) { } }
                  return;
                }

                if (inst) { try { inst.resume(); } catch (e) { } return; }

                // 场景已经没了（挂载曾经超时），回到前台补挂一次
                var cur = window.__alifeWpCfg || {};
                var rt = document.getElementById('alife-wallpaper');

                if (rt && cur.liveScene && cur.pkg && cur.lib && !document.getElementById('alife-wp-scene'))
                  wpMountLiveScene(rt, cur);
              });
            }

            if (document.hidden) { wpArmMountRetry(); return; }

            var host = document.createElement('div');
            host.id = 'alife-wp-scene';
            host.style.cssText = 'position:absolute;inset:0;opacity:0;transition:opacity .45s ease;';
            root.appendChild(host);

            var alive = function () { return document.getElementById('alife-wp-scene') === host; };
            var started = Date.now();

            wpLoadLib(cfg.lib).then(function (lib) {
              if (!alive()) return null;

              if (typeof lib.mount !== 'function' || typeof lib.bytesSource !== 'function')
                throw new Error('WebWallGL API 不匹配：' + Object.keys(lib).join(','));

              return fetch(cfg.pkg, { cache: 'no-store' }).then(function (r) {
                if (!r.ok) throw new Error('scene.pkg HTTP ' + r.status);
                return r.arrayBuffer();
              }).then(function (buf) {
                if (!alive()) return null;

                return Promise.resolve(lib.mount(host, {
                  source: lib.bytesSource(new Uint8Array(buf), null, cfg.key || cfg.pkg),
                  fit: cfg.fit === 'contain' ? 'contain' : 'cover',
                  fps: 60,
                  volume: 0,
                  scripts: cfg.scripts !== false,   // 关掉就没有属性通路：数值只会被暂存，永远不生效
                  // 只下发用户真的改过的值，其余留给场景自己的默认值
                  properties: (cfg.properties && Object.keys(cfg.properties).length) ? cfg.properties : undefined,
                  mountTimeoutMs: 60000,   // 200MB+ 的 pkg 首帧本来就要十几秒，15s 太紧
                  onReady: function () {
                    if (!alive()) return;
                    host.style.opacity = '1';
                    console.log('[wallpaper] 场景首帧 ' + (Date.now() - started) + 'ms');
                  },
                  onError: function (err) {
                    console.warn('[wallpaper] 场景渲染失败，保留预览图：' + ((err && err.message) || err));
                    wpDisposeScene();
                  }
                }));
              });
            }).then(function (inst) {
              if (!inst) return;
              if (!alive()) { try { inst.destroy({ releasePkgCache: true }); } catch (e) { } return; }
              window.__alifeWpScene = inst;

              // 挂载那一刻场景对象还没建好，紧接着下发的属性会被直接丢掉，
              // 场景随后用自己的默认值渲染 —— 用户存下来的开关就全都"没效果"。
              // 所以在场景起来之后再补一次，并且没打通原生通道就重试几次。
              var seed = cfg.propsNative || cfg.properties;
              if (!seed || !Object.keys(seed).length) return;

              var tries = 0;

              (function reseed() {
                if (!alive()) return;

                var r = window.__alifeWallpaperSetProps(seed);
                if (r.indexOf('native') >= 0 || r.indexOf('web') >= 0) return;
                if (++tries >= 8) { console.warn('[wallpaper] 属性种子下发失败：' + r); return; }

                setTimeout(reseed, 350);
              })();
            }).catch(function (e) {
              var msg = (e && e.message) || String(e);
              console.warn('[wallpaper] 场景实时渲染不可用：' + msg);
              wpDisposeScene();

              // 首帧超时 + 当时窗口不在前台：等切回来再挂，别就此停成一张静态图
              if (msg.indexOf('首帧超时') >= 0) wpArmMountRetry();
            });
          }

          // ---------------- 鼠标视差 ----------------
          // 光标相对屏幕中心偏移 → 壁纸轻微反向位移（关于中心对称）。
          // 每帧只写 transform，位移收敛完就停 rAF，静止时零开销。
          // 顺带把归一化坐标转给场景引擎，人物/镜头才会跟着鼠标动。
          (function wpParallax() {
            var depth = 0;
            var tx = 0, ty = 0;
            var cx = 0, cy = 0;
            var raf = 0;

            function apply() {
              var root = document.getElementById('alife-wallpaper');
              if (!root) return;

              if (depth <= 0.001) { root.style.transform = ''; return; }

              var px = -cx * depth * 0.018 * (window.innerWidth || 1200);
              var py = -cy * depth * 0.018 * (window.innerHeight || 800);
              var scale = 1 + depth * 0.03;   // 补边：位移后四角不能露底

              root.style.transformOrigin = '50% 50%';
              root.style.transform = 'translate3d(' + px.toFixed(2) + 'px,' + py.toFixed(2) + 'px,0) scale(' + scale.toFixed(4) + ')';
            }

            function step() {
              raf = 0;
              cx += (tx - cx) * 0.12;
              cy += (ty - cy) * 0.12;
              apply();

              if (Math.abs(tx - cx) > 0.002 || Math.abs(ty - cy) > 0.002) raf = requestAnimationFrame(step);
            }

            function kick() { if (depth > 0 && !raf) raf = requestAnimationFrame(step); }

            window.__alifeWpSetParallax = function (pct) {
              var next = Math.max(0, Math.min(100, +pct || 0)) / 100;
              if (next === depth) return;

              depth = next;
              if (depth <= 0.001) { tx = ty = cx = cy = 0; apply(); return; }
              kick();
            };

            document.addEventListener('pointermove', function (e) {
              var w = window.innerWidth || 1, h = window.innerHeight || 1;

              tx = (e.clientX / w) * 2 - 1;
              ty = (e.clientY / h) * 2 - 1;

              var inst = window.__alifeWpScene;
              if (inst && typeof inst.pushPointer === 'function') {
                try { inst.pushPointer(e.clientX / w, e.clientY / h, e.buttons || 0, 0); } catch (err) { }
              }

              kick();
            }, { passive: true });

            document.addEventListener('pointerleave', function () {
              tx = 0; ty = 0;
              kick();
            }, { passive: true });
          })();

          window.__alifeWallpaperShow = function (cfg) {
            if (!document.body) return 'wait';

            var root = document.getElementById('alife-wallpaper');
            if (!root) {
              root = document.createElement('div');
              root.id = 'alife-wallpaper';
              document.body.insertBefore(root, document.body.firstChild);
            }

            var style = document.getElementById('alife-wallpaper-style');
            if (!style) {
              style = document.createElement('style');
              style.id = 'alife-wallpaper-style';
              document.head.appendChild(style);
            }

            window.__alifeWpCfg = cfg;
            if (window.__alifeWpSetParallax) window.__alifeWpSetParallax(cfg.parallax);
            wpApplyStyle(cfg);

            // 透明化和挂件必须在 key 早退之前做：壁纸没换时也要保证它们存在
            if (window.__alifeWallpaperTransparent) window.__alifeWallpaperTransparent();
            if (window.__alifeWallpaperWidget) window.__alifeWallpaperWidget(!!cfg.widget, cfg.api, cfg.petAvatar, cfg.petSize, cfg.widgetX, cfg.widgetY, cfg.wpanel);

            // 刚重载 / 刚换页时 DOM 还在长，补几遍，别让人盯着白底等：
            // 0ms 已经涂过，这里补 200 / 800 / 1800ms 三次（之后交给 MutationObserver）
            var repaint = [200, 800, 1800];
            for (var rp = 0; rp < repaint.length; rp++) {
              (function (delay) {
                setTimeout(function () {
                  if (window.__alifeWallpaperTransparent) window.__alifeWallpaperTransparent();
                }, delay);
              })(repaint[rp]);
            }

            if (root.dataset.key === cfg.key) return 'same';
            root.dataset.key = cfg.key;

            // 换壁纸：先把上一张场景的 GL 资源、pkg 缓存和画布一起清掉
            wpDisposeScene();

            while (root.firstChild) root.removeChild(root.firstChild);

            if (cfg.kind === 'video' && cfg.url) {
              var v = document.createElement('video');
              v.autoplay = true;
              v.loop = true;
              v.muted = !!cfg.mute;
              v.playsInline = true;
              v.style.objectFit = cfg.fit === 'contain' ? 'contain' : 'cover';
              // CORS 取流（服务端已带 Access-Control-Allow-Origin）：挂了 WebAudio 增益后
              // 音量才能推到 200%，否则 MediaElementSource 对 no-cors 媒体会直接输出静音
              v.crossOrigin = 'anonymous';
              v.volume = Math.max(0, Math.min(1, (cfg.volume | 0) / 100));
              var vrate = Math.max(0.5, Math.min(2, cfg.rate || 1));
              v.defaultPlaybackRate = vrate;
              v.src = cfg.url;
              v.playbackRate = vrate;
              root.appendChild(v);
              var p = v.play();
              if (p && p.catch) p.catch(function () { });

              // 记住的音量可能超过 100%：能接上 WebAudio 增益就立刻接上，
              // 否则重载后会一直停在 1.0，得手动拖一下滑块才生效
              wpWireGain(v, cfg.volume);
            } else if (cfg.kind === 'image' && cfg.url) {
              // 用户自己的静态壁纸 / GIF：原样铺满，不做 soften（那是给场景封面糊化用的）
              var still = document.createElement('img');
              still.style.objectFit = cfg.fit === 'contain' ? 'contain' : 'cover';
              still.decoding = 'async';
              still.src = cfg.url;
              root.appendChild(still);
            } else if (cfg.kind === 'web' && cfg.url) {
              var f = document.createElement('iframe');
              f.src = cfg.url;
              f.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-forms allow-popups');
              // 种子里已有值；这里再补一次，兜住「监听器比种子更晚注册」的壁纸
              f.addEventListener('load', function () { wpPostWebProps(cfg.propsNative || cfg.properties); });
              root.appendChild(f);
            } else if (cfg.preview) {
              var i = document.createElement('img');
              i.style.objectFit = cfg.fit === 'contain' ? 'contain' : 'cover';
              i.src = cfg.preview;
              // 场景壁纸只有 192~1024 的方形封面，硬拉满屏只会更糊；改成柔化的氛围层
              if (cfg.soften) {
                i.style.filter = 'blur(28px) saturate(1.2)';
                i.style.transform = 'scale(1.15)';
              }
              root.appendChild(i);

              // 场景壁纸：预览图先当底片，WebWallGL 首帧出来再淡入盖上去
              if (cfg.liveScene && cfg.pkg && cfg.lib) wpMountLiveScene(root, cfg);
            }

            // 切换过渡：淡入 / 硬切
            if (cfg.fade) {
              root.style.opacity = '0';
              requestAnimationFrame(function () {
                requestAnimationFrame(function () { root.style.opacity = '1'; });
              });
            } else {
              root.style.opacity = '1';
            }

            return 'ok';
          };

          // 只调参数（遮罩 / 面板 / 模糊）时走这里，不重建媒体元素
          window.__alifeWallpaperRestyle = function (cfg) {
            window.__alifeWpCfg = cfg;
            if (window.__alifeWpSetParallax) window.__alifeWpSetParallax(cfg.parallax);
            wpApplyStyle(cfg);
            // 挂件形象跟着设置面板即时变（挂件还没建好时，由创建流程拿 cfg 兜底）
            if (window.__alifeWpPet && cfg.petAvatar !== undefined) {
              try { window.__alifeWpPet(cfg.petAvatar, cfg.petSize); } catch (e) { }
            }
            // 面板不透明度：拖滑块时立刻看到效果，不必等下一次 state 轮询
            if (window.__alifeWpPanelLook && cfg.wpanel !== undefined) {
              try { window.__alifeWpPanelLook(cfg.wpanel); } catch (e) { }
            }
            if (window.__alifeWallpaperRestore) window.__alifeWallpaperRestore();
            if (window.__alifeWallpaperTransparent) window.__alifeWallpaperTransparent();
            return 'ok';
          };

          // 只改媒体参数（静音 / 音量 / 倍速），不影响壁纸层本身
          // 网页壁纸：把属性 postMessage 进 iframe（跨源，碰不到它的 document）
          function wpPostWebProps(props) {
            var frame = document.querySelector('#alife-wallpaper iframe');
            if (!frame || !frame.contentWindow) return false;

            try {
              frame.contentWindow.postMessage({ __alifeWp: 1, type: 'props', props: props || {} }, '*');
              return true;
            } catch (e) {
              return false;
            }
          }

          // 引擎的原生热更新在内部对象上：inst 只是 facade（没有 declareUserProperties），
          // 内部对象通过 globalThis[Symbol.for('webwallgl.runtime-link')] 这张 WeakMap 关联。
          // declareUserProperties 才是「颜色 / 大小 / 位置 / 可见 / 文字」真正的重算入口；
          // setProperties 只把值转给场景脚本，画面不会动。
          function wpEditor(inst) {
            try {
              var link = globalThis[Symbol.for('webwallgl.runtime-link')];
              var ctx = (link && link.get) ? link.get(inst) : null;
              var ed = ctx && ctx.sceneCtl && ctx.sceneCtl.editor;
              return (ed && typeof ed.declareUserProperties === 'function') ? ed : null;
            } catch (e) { return null; }
          }

          window.__alifeWallpaperSetProps = function (props) {
            var done = [];
            var cfg = window.__alifeWpCfg || {};
            var incoming = props || {};

            // 引擎只认 WE 的节点形状 { value, type }：裸标量不会被登记进场景属性表，
            // 图层绑定（visible / color / origin / scale…）随后全都解析成 null 退回默认值
            var nodes = {}, scalars = {};

            for (var k in incoming) {
              if (!Object.prototype.hasOwnProperty.call(incoming, k)) continue;

              var v = incoming[k];

              if (v && typeof v === 'object' && 'value' in v) { nodes[k] = v; scalars[k] = v.value; }
              else { nodes[k] = { value: v, type: 'textinput' }; scalars[k] = v; }
            }

            var inst = window.__alifeWpScene;
            if (inst) {
              var ed = wpEditor(inst);

              if (ed) {
                try {
                  var task = ed.declareUserProperties(nodes);
                  if (task && task.catch) task.catch(function () { });
                  done.push('native');
                }
                catch (e) { done.push('native-err:' + (e && e.message)); }
              } else if (cfg.kind !== 'web') {
                done.push('no-native');
              }

              // 再喂一遍脚本：逻辑 / 动画类属性要靠脚本的 applyUserProperties
              if (typeof inst.setProperties === 'function') {
                try { inst.setProperties(scalars); done.push('script'); }
                catch (e) { done.push('script-err:' + (e && e.message)); }
              }

              // 脚本关着时引擎只把值存下来，画面上不会有任何变化——得让面板说清楚
              if (cfg.scripts === false) done.push('scripts-off');
            } else if (cfg.kind !== 'web') {
              // 场景没挂上引擎（还在加载 / 已退回静态预览图）
              done.push('no-scene');
            }

            if (wpPostWebProps(nodes)) done.push('web');

            return done.join(',') || 'no-target';
          };

          // 音量 0-200：100 以上靠 WebAudio 增益推，元素本身固定 1.0
          function wpWireGain(v, pct) {
            if (!v) return false;

            if (!window.__alifeWpAudioCtx) {
              var Ctx = window.AudioContext || window.webkitAudioContext;
              if (!Ctx) return false;
              try { window.__alifeWpAudioCtx = new Ctx(); } catch (e) { return false; }
            }

            var ctx = window.__alifeWpAudioCtx;
            if (ctx.state === 'suspended') { try { ctx.resume(); } catch (e) { } }
            if (ctx.state !== 'running') return false;

            try {
              if (!v.__wpGain) {
                var src = ctx.createMediaElementSource(v);
                var gain = ctx.createGain();
                src.connect(gain);
                gain.connect(ctx.destination);
                v.__wpGain = gain;
                v.volume = 1;
              }

              v.__wpGain.gain.value = Math.max(0, Math.min(2, (+pct || 0) / 100));
              return true;
            } catch (e) {
              return false;
            }
          }

          // 浏览器策略下 AudioContext 要用户点一下才 running：点任意处就恢复并接上增益
          document.addEventListener('pointerdown', function () {
            var ctx = window.__alifeWpAudioCtx;
            if (!ctx || ctx.state !== 'suspended') return;
            ctx.resume().then(function () {
              var v = document.querySelector('#alife-wallpaper video');
              var cfg = window.__alifeWpCfg || {};
              if (v) wpWireGain(v, cfg.volume);
            }).catch(function () { });
          }, { passive: true });

          window.__alifeWallpaperMedia = function (cfg) {
            var root = document.getElementById('alife-wallpaper');
            var v = root ? root.querySelector('video') : null;
            if (!v) return 'no-media';

            if (cfg.mute != null) {
              v.muted = !!cfg.mute;
              // 取消静音时如果视频被策略暂停了，顺手拉起来，否则听着就是“没声音”
              if (!v.muted && v.paused) {
                var p = v.play();
                if (p && p.catch) p.catch(function () { });
              }
            }

            if (cfg.volume != null) {
              var pct = Math.max(0, Math.min(200, cfg.volume | 0));

              // 先试 WebAudio 增益（支持 >100%）；接不上就退回元素自己的音量
              if (!wpWireGain(v, pct)) v.volume = Math.min(1, pct / 100);
            }

            if (cfg.rate != null) {
              var r = Math.max(0.5, Math.min(2, +cfg.rate));
              v.defaultPlaybackRate = r;   // 万一片源重载，倍速也跟着回来
              v.playbackRate = r;
            }

            return 'ok';
          };

          window.__alifeWallpaperClear = function (keepWidget) {
            wpDisposeScene();

            if (window.__alifeWpObs) {
              try { window.__alifeWpObs.disconnect(); } catch (e) { }
              window.__alifeWpObs = null;
            }

            // 用户主动“清除壁纸”时保留挂件，否则挂件没了就再也叫不回来
            if (!keepWidget && window.__alifeWallpaperWidget) window.__alifeWallpaperWidget(false);
            if (window.__alifeWallpaperRestore) window.__alifeWallpaperRestore();

            var root = document.getElementById('alife-wallpaper');
            if (root && root.parentNode) root.parentNode.removeChild(root);

            var style = document.getElementById('alife-wallpaper-style');
            if (style && style.parentNode) style.parentNode.removeChild(style);
          };

          // 这些元素自身的底色要保留（输入框、按钮、图标…），不做透明化
          var WP_SKIP_TAGS = { INPUT: 1, TEXTAREA: 1, SELECT: 1, OPTION: 1, BUTTON: 1, IMG: 1, VIDEO: 1, CANVAS: 1, SVG: 1, PATH: 1, PRE: 1, CODE: 1, HR: 1 };
          // 聊天气泡 / 提示浮层：只排除气泡本身，别再误伤 "message-input" 这类容器
          var WP_SKIP_CLASS = /(^|[\s_-])(bubble|message-bubble|message-content|msg-bubble|chat-bubble|tooltip|popover|toast)([\s_-]|$)/i;

          function wpSkipped(el) {
            if (WP_SKIP_TAGS[el.tagName]) return true;
            if (el.isContentEditable) return true;

            for (var n = el, k = 0; n && k < 4; n = n.parentElement, k++) {
              if (typeof n.className === 'string' && WP_SKIP_CLASS.test(n.className)) return true;
            }
            return false;
          }

          // 计算样式是“不透明 + 接近纯白/浅灰”才算面板底色（内联、class、CSS 变量都能覆盖到）
          function wpIsOpaqueLight(el) {
            var bg;
            try { bg = getComputedStyle(el).backgroundColor || ''; } catch (e) { return false; }

            var m = bg.match(/^rgba?\(([^)]+)\)$/);
            if (!m) return false;

            var p = m[1].split(',');
            if (p.length < 3) return false;

            var a = p.length > 3 ? parseFloat(p[3]) : 1;
            if (!(a >= 0.5)) return false;

            var r = parseFloat(p[0]), g = parseFloat(p[1]), b = parseFloat(p[2]);
            if (!(r >= 236 && g >= 236 && b >= 236)) return false;   // 只要接近白
            return (Math.max(r, g, b) - Math.min(r, g, b)) <= 24;    // 允许一点浅红/浅蓝底
          }

          // 扫描范围：主内容区优先，没有就退到整个应用容器（左右两栏白卡不一定都在 .main-content 里）
          var WP_PANELS = 'div,section,aside,form,ul,nav,header,footer,table';

          // 页面切来切去会重建 DOM，新节点上的磨砂就丢了；
          // 用 MutationObserver 盯着 body，有新增节点就自动补一次（不监听属性，避免自己改样式触发死循环）
          function wpObserve() {
            if (window.__alifeWpObs || !document.body || !window.MutationObserver) return;

            var timer = 0;
            window.__alifeWpObs = new MutationObserver(function (records) {
              var added = 0;
              for (var i = 0; i < records.length; i++) added += records[i].addedNodes.length;
              if (!added) return;

              clearTimeout(timer);
              timer = setTimeout(function () {
                if (window.__alifeWallpaperTransparent) window.__alifeWallpaperTransparent();
              }, 300);
            });

            window.__alifeWpObs.observe(document.body, { childList: true, subtree: true });
          }

          // 把白色容器换成半透明白（磨砂），原底色记在元素上，关闭壁纸时还原。
          // 嵌套容器只让最外层出白：否则每层都叠 45% 白，越往里越白（聊天输入区就是这样变白的）
          function wpFrost(el, alpha) {
            if (el.__alifeWpBg !== undefined) return false;

            var value = 'rgba(255,255,255,' + alpha + ')';
            for (var n = el.parentElement; n; n = n.parentElement) {
              if (n.__alifeWpBg !== undefined) { value = 'transparent'; break; }
            }

            try { el.__alifeWpBg = getComputedStyle(el).backgroundColor; }
            catch (e) { el.__alifeWpBg = 'rgb(255, 255, 255)'; }

            // 加个过渡，避免切页时“啪”一下由白变磨砂
            if (!el.style.transition) el.style.setProperty('transition', 'background-color .16s ease');

            el.style.setProperty('background', value, 'important');
            return true;
          }

          // ---------------- 聊天气泡 / 彩色提示卡：独立的一条透明度 ----------------
          // 「白度」只认近白底色，绿底的工具结果卡和纯白气泡都碰不到，所以单独走这里：
          // 保留原本的 RGB，只把 alpha 换掉，色相不变。
          function wpTintable(el) {
            if (!el || el.__alifeWpTint !== undefined) return false;
            if (WP_SKIP_TAGS[el.tagName]) return false;
            if (el.isContentEditable) return false;
            if (el.closest && (el.closest('#alife-wp-widget') || el.closest('.global-sidebar'))) return false;

            // 祖先已经涂过就不再涂，否则一层层叠加会越往里越透
            for (var n = el.parentElement, k = 0; n && k < 8; n = n.parentElement, k++) {
              if (n.__alifeWpTint !== undefined) return false;
            }
            return true;
          }

          function wpTint(el, alpha) {
            var bg;
            try { bg = getComputedStyle(el).backgroundColor || ''; } catch (e) { return false; }

            var m = bg.match(/^rgba?\(([^)]+)\)$/);
            if (!m) return false;

            var p = m[1].split(',');
            if (p.length < 3) return false;

            var a = p.length > 3 ? parseFloat(p[3]) : 1;
            if (!(a >= 0.35)) return false;          // 本来就透明，别管

            var r = parseFloat(p[0]), g = parseFloat(p[1]), b = parseFloat(p[2]);
            if (!isFinite(r) || !isFinite(g) || !isFinite(b)) return false;

            el.__alifeWpTint = bg;
            if (!el.style.transition) el.style.setProperty('transition', 'background-color .16s ease');
            el.style.setProperty('background-color', 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')', 'important');
            return true;
          }

          // 1) 聊天气泡（.chat-bubble / .chat-bubble-ai / .chat-bubble-user …）
          // 2) 彩色且圆角的色块（工具结果那种绿底卡），近白面板不在这里处理
          function wpTintBubbles(alpha) {
            if (!(alpha < 0.999) || !document.body) return 0;

            var done = 0, i;

            var bubbles = document.body.querySelectorAll('[class*="chat-bubble"]');
            for (i = 0; i < bubbles.length; i++) {
              var el = bubbles[i];
              if (!wpTintable(el)) continue;
              if (el.clientWidth < 60 || el.clientHeight < 20) continue;
              if (wpTint(el, alpha)) done++;
            }

            var blocks = document.body.querySelectorAll('div,section');
            for (i = 0; i < blocks.length; i++) {
              var c = blocks[i];
              if (!wpTintable(c)) continue;

              var w = c.clientWidth, h = c.clientHeight;
              if (w < 180 || h < 32) continue;

              var bg, radius = 0;
              try {
                var cs = getComputedStyle(c);
                bg = cs.backgroundColor || '';
                radius = parseFloat(cs.borderTopLeftRadius) || 0;
              } catch (e) { continue; }

              if (radius < 4) continue;                // 方正的布局块不算气泡

              var m = bg.match(/^rgba?\(([^)]+)\)$/);
              if (!m) continue;

              var q = m[1].split(',');
              if (q.length < 3) continue;

              var a = q.length > 3 ? parseFloat(q[3]) : 1;
              if (!(a >= 0.5)) continue;

              var r = parseFloat(q[0]), g = parseFloat(q[1]), b = parseFloat(q[2]);
              if (!isFinite(r) || !isFinite(g) || !isFinite(b)) continue;

              if (r >= 236 && g >= 236 && b >= 236) continue;                 // 近白 → 归「白度」管
              if (Math.max(r, g, b) - Math.min(r, g, b) < 22) continue;       // 灰阶 → 不是彩色卡

              if (wpTint(c, alpha)) done++;
            }

            return done;
          }

          // 把“面板级白色容器”变透明：面积 >= 视口 1.5% 且高 >= 80，
          // 再把大块多行编辑区（textarea / contenteditable）也磨砂掉，否则整块白会挡住壁纸。
          // 聊天气泡（矮）不受影响。
          window.__alifeWallpaperTransparent = function () {
            var vw = window.innerWidth || 1920;
            var vh = window.innerHeight || 1080;

            // 记账：能看出这个函数到底有没有被反复调用、每次涂了几个
            window.__alifeWpTick = (window.__alifeWpTick || 0) + 1;
            window.__alifeWpLast = Date.now();
            window.__alifeWpStat = { total: 0, picked: 0, frosted: 0 };

            // 面板磨砂强度：0 = 完全透明，100 = 保持原来的白色（太低会看不清消息）
            var alpha = 0.45;
            try {
              if (window.__alifeWpCfg && window.__alifeWpCfg.panel != null) alpha = +window.__alifeWpCfg.panel;
            } catch (e) { }
            alpha = Math.max(0, Math.min(1, alpha));

            // 直接扫整个 body：Alife 切页后可能同时存在多个 .app-container，
            // 只取第一个会漏掉新页面的容器（聊天输入区就是这么漏掉的）
            var scope = document.body;
            if (!scope) return 'ok';

            // 先收集再统一涂：避免边读布局边改样式，几千个节点时不会把主线程拖住
            var picked = [];

            var nodes = scope.querySelectorAll(WP_PANELS);
            for (var i = 0; i < nodes.length; i++) {
              var el = nodes[i];
              if (el.__alifeWpBg !== undefined) continue;      // 已经处理过

              window.__alifeWpStat.total++;

              var w = el.clientWidth, h = el.clientHeight;
              if (w < 40 || h < 20) {
                window.__alifeWpStat.small = (window.__alifeWpStat.small || 0) + 1;
                continue;
              }
              if (wpSkipped(el)) {
                window.__alifeWpStat.skipped = (window.__alifeWpStat.skipped || 0) + 1;
                continue;
              }
              if (!wpIsOpaqueLight(el)) {
                window.__alifeWpStat.rejColor = window.__alifeWpStat.rejColor || [];
                if (window.__alifeWpStat.rejColor.length < 8) {
                  var rejBg = '?';
                  try { rejBg = getComputedStyle(el).backgroundColor; } catch (e) { }
                  window.__alifeWpStat.rejColor.push((el.className || '(no class)') + ' ' + w + 'x' + h + ' ' + rejBg);
                }
                continue;
              }

              picked.push(el);
            }

            // 大的文本编辑区（角色设定 / 聊天输入框那种），否则它就是一整块白板
            var fields = scope.querySelectorAll('textarea,[contenteditable="true"],[contenteditable=""]');
            for (var j = 0; j < fields.length; j++) {
              var field = fields[j];
              if (field.__alifeWpBg !== undefined) continue;
              if (field.clientWidth < 160 || field.clientHeight < 40) continue;
              if (!wpIsOpaqueLight(field)) continue;

              picked.push(field);
            }

            // 输入框 / 按钮 / 数字框这些“小而白”的控件也统一磨砂，否则每个都是一块白斑
            var controls = scope.querySelectorAll('input,select,button,.ant-input-number,.ant-select-selector,.ant-input-affix-wrapper');
            for (var k = 0; k < controls.length; k++) {
              var ctl = controls[k];
              if (ctl.__alifeWpBg !== undefined) continue;
              if (ctl.clientWidth < 24 || ctl.clientHeight < 16) continue;
              if (!wpIsOpaqueLight(ctl)) continue;

              picked.push(ctl);
            }

            window.__alifeWpStat.picked = picked.length;

            var done = 0;
            for (var m = 0; m < picked.length; m++) {
              if (wpFrost(picked[m], alpha)) done++;
            }

            window.__alifeWpStat.frosted = done;

            // 气泡 / 彩色提示卡：独立透明度，跟「白度」互不干扰
            var bubbleAlpha = 0.65;
            try {
              if (window.__alifeWpCfg && window.__alifeWpCfg.bubble != null) bubbleAlpha = +window.__alifeWpCfg.bubble;
            } catch (e) { }
            bubbleAlpha = Math.max(0, Math.min(1, bubbleAlpha));
            window.__alifeWpStat.tinted = wpTintBubbles(bubbleAlpha);

            window.__alifeWpStat.alpha = alpha;

            wpObserve();

            return window.__alifeWpStat;
          };

          // 供外部排查：把页面上所有“浅色不透明”的元素列出来（服务端 /api/dom 用）
          window.__alifeWallpaperDump = function () {
            var scope = document.body;
            var all = scope ? scope.querySelectorAll('*') : [];
            var out = [];

            for (var i = 0; i < all.length; i++) {
              var el = all[i];
              var w = el.clientWidth, h = el.clientHeight;
              if (w < 60 || h < 24) continue;

              var bg;
              try { bg = getComputedStyle(el).backgroundColor || ''; } catch (e) { continue; }

              var m = bg.match(/^rgba?\(([^)]+)\)$/);
              if (!m) continue;

              var p = m[1].split(',');
              var a = p.length > 3 ? parseFloat(p[3]) : 1;
              if (!(a >= 0.15)) continue;

              var r = parseFloat(p[0]), g = parseFloat(p[1]), b = parseFloat(p[2]);
              if (!(r >= 200 && g >= 200 && b >= 200)) continue;

              out.push(el.tagName.toLowerCase() + ' | ' +
                       (typeof el.className === 'string' ? el.className.slice(0, 100) : '') + ' | ' +
                       (el.id || '') + ' | ' + w + 'x' + h + ' | ' + bg +
                       (el.__alifeWpBg !== undefined ? ' | frosted' : ''));
            }

            var wb = document.getElementById('alife-wp-widget');
            var rect = wb ? wb.getBoundingClientRect() : null;

            return JSON.stringify({
              ver: window.__alifeWpVer || '',
              tick: window.__alifeWpTick || 0,
              lastRunMsAgo: window.__alifeWpLast ? (Date.now() - window.__alifeWpLast) : -1,
              stat: window.__alifeWpStat || null,
              widget: !!wb,
              widgetRect: rect ? [Math.round(rect.left), Math.round(rect.top), Math.round(rect.width), Math.round(rect.height)] : null,
              vw: window.innerWidth,
              vh: window.innerHeight,
              count: out.length,
              lines: out.slice(0, 200)
            });
          };

          window.__alifeWallpaperRestore = function () {
            var all = document.body ? document.body.querySelectorAll('*') : [];
            for (var i = 0; i < all.length; i++) {
              var el = all[i];

              if (el.__alifeWpTint !== undefined) {
                el.style.setProperty('background-color', el.__alifeWpTint, 'important');
                el.__alifeWpTint = undefined;
              }

              if (el.__alifeWpBg === undefined) continue;

              el.style.setProperty('background', el.__alifeWpBg, 'important');
              el.__alifeWpBg = undefined;
            }
            return 'ok';
          };

          // ---------------- 右下角壁纸挂件 ----------------
          window.__alifeWallpaperWidget = function (on, api, petAvatar, petSize, widgetX, widgetY, wpanel) {
            var box = document.getElementById('alife-wp-widget');

            if (!on) {
              if (box && box.parentNode) box.parentNode.removeChild(box);
              var old = document.getElementById('alife-wp-widget-style');
              if (old && old.parentNode) old.parentNode.removeChild(old);
              return 'off';
            }

            if (api) window.__alifeWpApi = api;
            if (box) return 'exists';

            var css = document.getElementById('alife-wp-widget-style');
            if (!css) {
              css = document.createElement('style');
              css.id = 'alife-wp-widget-style';
              css.textContent =
                '#alife-wp-widget{position:fixed;right:18px;bottom:112px;z-index:2147483645;font:13px/1.45 system-ui,"Microsoft YaHei",sans-serif;}' +
                '#alife-wp-widget *{box-sizing:border-box;}' +
                '#alife-wp-widget .wp-trigger{display:flex;align-items:center;gap:6px;height:38px;padding:0 14px 0 11px;margin-left:auto;border-radius:19px;border:1px solid rgba(255,255,255,.28);background:rgba(22,22,28,.86);color:#eaeaef;font-size:12px;cursor:pointer;box-shadow:0 8px 22px rgba(0,0,0,.45);backdrop-filter:blur(10px);transition:background .15s;}' +
                '#alife-wp-widget .wp-trigger:hover{background:rgba(44,44,56,.92);}' +
                // Q 版形象挂件：把胶囊整个换成透明贴图，点开还是同一个面板
                '#alife-wp-widget .wp-pet-img{display:none;width:100%;height:100%;object-fit:contain;pointer-events:none;user-select:none;-webkit-user-drag:none;filter:drop-shadow(0 7px 16px rgba(0,0,0,.5));}' +
                '#alife-wp-widget .wp-trigger.pet{background:transparent;border:0;box-shadow:none;padding:0;height:auto;border-radius:0;backdrop-filter:none;-webkit-backdrop-filter:none;display:flex;align-items:center;justify-content:center;transition:transform .16s ease;}' +
                '#alife-wp-widget .wp-trigger.pet:hover{background:transparent;transform:scale(1.06);}' +
                '#alife-wp-widget .wp-trigger.pet .wp-pet-img{display:block;}' +
                '#alife-wp-widget .wp-trigger.pet .wp-pet-fallback{display:none;}' +
                '#alife-wp-widget .wp-trigger.pet.failed .wp-pet-img{display:none;}' +
                '#alife-wp-widget .wp-trigger.pet.failed .wp-pet-fallback{display:block;}' +
                '#alife-wp-widget .wp-trigger.pet.failed{background:rgba(22,22,28,.86);border:1px solid rgba(255,255,255,.28);box-shadow:0 8px 22px rgba(0,0,0,.45);padding:0 14px 0 11px;height:38px;border-radius:19px;}' +
                '#alife-wp-widget .wp-panel{position:absolute;right:0;bottom:54px;width:436px;max-width:calc(100vw - 24px);max-height:min(86vh,780px);display:none;flex-direction:column;border-radius:16px;background:rgba(18,18,23,.55);border:1px solid rgba(255,255,255,.12);box-shadow:0 18px 44px rgba(0,0,0,.52);backdrop-filter:blur(9px);color:#e9e9ef;overflow:hidden;}' +
                '#alife-wp-widget.open .wp-panel{display:flex;}' +
                // 面板调到很透时，文字加深色描边才不会跟壁纸糊在一起
                '#alife-wp-widget .wp-panel.wp-thin{text-shadow:0 1px 2px rgba(0,0,0,.95),0 0 4px rgba(0,0,0,.85);border-color:rgba(255,255,255,.22);}' +
                '#alife-wp-widget .wp-panel.wp-thin .wp-item,#alife-wp-widget .wp-panel.wp-thin .wp-prop-row,#alife-wp-widget .wp-panel.wp-thin .wp-prop-slider,#alife-wp-widget .wp-panel.wp-thin .wp-prop-line{background:rgba(12,12,16,.35);}' +
                '#alife-wp-widget .wp-head{display:flex;align-items:center;gap:8px;padding:10px 12px;border-bottom:1px solid rgba(255,255,255,.08);}' +
                '#alife-wp-widget .wp-head b{font-size:13px;font-weight:600;}' +
                '#alife-wp-widget .wp-head span{flex:1;color:#8b8b99;font-size:11px;}' +
                '#alife-wp-widget .wp-scrim{border:1px solid rgba(255,255,255,.2);background:rgba(255,255,255,.08);color:#cfd3dd;font-size:11px;padding:3px 8px;border-radius:8px;cursor:pointer;}' +
                '#alife-wp-widget .wp-scrim:hover{background:rgba(255,255,255,.16);}' +
                '#alife-wp-widget .wp-close{border:0;background:transparent;color:#9a9aa8;font-size:17px;line-height:1;cursor:pointer;padding:2px 4px;}' +
                '#alife-wp-widget .wp-close:hover{color:#fff;}' +
                '#alife-wp-widget .wp-sliders{padding:8px 12px;display:grid;gap:7px;border-bottom:1px solid rgba(255,255,255,.08);}' +
                '#alife-wp-widget .wp-sliders label{display:grid;grid-template-columns:38px 1fr 30px;align-items:center;gap:8px;font-size:11px;color:#a8a8b6;}' +
                '#alife-wp-widget .wp-sliders i{font-style:normal;text-align:right;color:#d6d6e0;}' +
                '#alife-wp-widget .wp-row{display:flex;align-items:center;gap:8px;font-size:11px;color:#a8a8b6;}' +
                '#alife-wp-widget .wp-row button{margin-left:auto;border:1px solid rgba(255,255,255,.2);background:rgba(255,255,255,.08);color:#cfd3dd;font-size:11px;padding:3px 10px;border-radius:8px;cursor:pointer;}' +
                '#alife-wp-widget .wp-row button.on{background:rgba(124,196,255,.22);border-color:rgba(124,196,255,.55);color:#dff0ff;}' +
                '#alife-wp-widget .wp-foot{padding:8px 12px;border-top:1px solid rgba(255,255,255,.08);display:flex;gap:8px;}' +
                '#alife-wp-widget .wp-clear{flex:1;border:1px solid rgba(255,120,120,.35);background:rgba(255,80,80,.12);color:#ffc9c9;font-size:12px;padding:6px;border-radius:9px;cursor:pointer;}' +
                '#alife-wp-widget .wp-home{border:1px solid rgba(124,196,255,.35);background:rgba(124,196,255,.12);color:#cfe6ff;font-size:12px;padding:6px 10px;border-radius:9px;cursor:pointer;white-space:nowrap;}' +
                '#alife-wp-widget .wp-clear:hover{background:rgba(255,80,80,.24);}' +
                '#alife-wp-widget input[type=range]{-webkit-appearance:none;height:3px;border-radius:2px;background:rgba(255,255,255,.2);outline:none;}' +
                '#alife-wp-widget input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;width:11px;height:11px;border-radius:50%;background:#7cc4ff;cursor:pointer;}' +
                '#alife-wp-widget input[type=range]:disabled{opacity:.32;}' +
                '#alife-wp-widget input[type=range]:disabled::-webkit-slider-thumb{background:#8b8b99;cursor:not-allowed;}' +
                '#alife-wp-widget .wp-row button:disabled{opacity:.4;cursor:not-allowed;}' +
                '#alife-wp-widget .wp-tabs{display:flex;gap:6px;padding:8px 10px 0;}' +
                '#alife-wp-widget .wp-tab{flex:1;border:1px solid rgba(255,255,255,.14);background:rgba(255,255,255,.05);color:#b9bcc8;font-size:12px;padding:6px 0;border-radius:9px;cursor:pointer;}' +
                '#alife-wp-widget .wp-tab:hover{background:rgba(255,255,255,.1);}' +
                '#alife-wp-widget .wp-tab.on{background:rgba(124,196,255,.24);border-color:rgba(124,196,255,.6);color:#e8f4ff;font-weight:600;}' +
                '#alife-wp-widget .wp-tab em{font-style:normal;font-size:11px;opacity:.8;margin-left:3px;}' +
                '#alife-wp-widget .wp-hint{color:#7f8493;font-size:11px;padding:2px 3px 6px;}' +
                // 作者的分组标题经常是一整串没有空格的 BBCode/路径（1700px 宽），
                // 不换行就会把列表撑出横向滚动条，右侧一片空白
                '#alife-wp-widget .wp-prop-title{margin:7px 2px 3px;padding-bottom:3px;border-bottom:1px solid rgba(255,255,255,.1);color:#9fd0ff;font-size:11px;font-weight:600;white-space:pre-line;overflow-wrap:anywhere;word-break:break-word;max-width:100%;}' +
                '#alife-wp-widget .wp-prop-row{display:flex;align-items:center;gap:8px;font-size:11px;color:#c7c7d4;padding:3px 2px;}' +
                '#alife-wp-widget .wp-prop-row span{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                '#alife-wp-widget .wp-prop-row button{border:1px solid rgba(255,255,255,.2);background:rgba(255,255,255,.08);color:#cfd3dd;font-size:11px;padding:2px 9px;border-radius:7px;cursor:pointer;}' +
                '#alife-wp-widget .wp-prop-row button.on{background:rgba(124,196,255,.22);border-color:rgba(124,196,255,.55);color:#dff0ff;}' +
                '#alife-wp-widget .wp-prop-slider{display:grid;grid-template-columns:1fr 40px 20px;align-items:center;gap:6px;font-size:11px;color:#c7c7d4;padding:3px 2px;}' +
                '#alife-wp-widget .wp-prop-slider input{grid-column:1 / -1;width:100%;}' +
                '#alife-wp-widget .wp-prop-slider i{justify-self:end;color:#9aa0ae;font-style:normal;}' +
                '#alife-wp-widget .wp-prop-line{display:flex;align-items:center;gap:8px;font-size:11px;color:#c7c7d4;padding:3px 2px;}' +
                '#alife-wp-widget .wp-prop-line span{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                '#alife-wp-widget .wp-prop-line select,#alife-wp-widget .wp-prop-line input[type=text]{flex:0 0 104px;min-width:0;background:rgba(0,0,0,.35);border:1px solid rgba(255,255,255,.18);color:#e9e9ef;font-size:11px;border-radius:7px;padding:2px 4px;}' +
                '#alife-wp-widget .wp-prop-line input[type=color]{flex:0 0 auto;width:34px;height:22px;padding:0;border:1px solid rgba(255,255,255,.18);background:transparent;border-radius:6px;cursor:pointer;}' +
                '#alife-wp-widget .wp-prop-reset{flex:0 0 auto;border:1px solid rgba(255,255,255,.16);background:transparent;color:#8d93a1;font-size:11px;line-height:1;padding:3px 5px;border-radius:6px;cursor:pointer;}' +
                '#alife-wp-widget .wp-prop-reset:hover{color:#dff0ff;border-color:rgba(124,196,255,.55);background:rgba(124,196,255,.14);}' +
                '#alife-wp-widget .wp-prop-reset.is-def{opacity:.32;}' +
                '#alife-wp-widget .wp-prop-actions{display:flex;align-items:center;gap:8px;padding:2px 2px 5px;font-size:11px;color:#7f8493;}' +
                '#alife-wp-widget .wp-prop-actions span{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                '#alife-wp-widget .wp-prop-actions button{border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.06);color:#cfd3dd;font-size:11px;padding:2px 9px;border-radius:7px;cursor:pointer;}' +
                '#alife-wp-widget .wp-prop-actions button:hover{background:rgba(255,255,255,.14);}' +
                '#alife-wp-widget .wp-warn{color:#ffcf9b;background:rgba(255,170,60,.1);border:1px solid rgba(255,170,60,.28);border-radius:8px;padding:5px 7px;margin:3px 2px 6px;line-height:1.5;white-space:normal;}' +
                '#alife-wp-widget .wp-list{flex:1 1 auto;min-height:0;overflow-y:auto;overflow-x:hidden;padding:6px;display:flex;flex-direction:column;gap:2px;}' +
                // 每一项都是「可点的正文 + 右侧动作按钮」：按钮多了要更高的行，宽度也跟着放大一档
                '#alife-wp-widget .wp-item{display:flex;align-items:center;gap:6px;padding:8px;border:0;border-radius:10px;background:transparent;color:#e9e9ef;width:100%;min-height:60px;}' +
                '#alife-wp-widget .wp-item:hover{background:rgba(255,255,255,.08);}' +
                '#alife-wp-widget .wp-item.current{background:rgba(124,196,255,.16);}' +
                '#alife-wp-widget .wp-pick{flex:1;min-width:0;display:flex;align-items:center;gap:9px;padding:0;border:0;background:transparent;color:inherit;font:inherit;cursor:pointer;text-align:left;}' +
                '#alife-wp-widget .wp-pick.aside{cursor:default;}' +
                '#alife-wp-widget .wp-item img,#alife-wp-widget .wp-noimg{width:66px;height:42px;border-radius:7px;object-fit:cover;flex:0 0 auto;background:rgba(255,255,255,.08);}' +
                '#alife-wp-widget .wp-col{flex:1;min-width:0;display:flex;flex-direction:column;gap:2px;}' +
                '#alife-wp-widget .wp-title{flex:1;font-size:12px;line-height:1.35;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                '#alife-wp-widget .wp-type{font-size:10px;color:#8b8b99;font-style:normal;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                // 动作按钮：平时半透明，悬停整行才完全亮起来，免得列表花
                '#alife-wp-widget .wp-acts{display:flex;align-items:center;gap:4px;flex:0 0 auto;opacity:.42;transition:opacity .15s;}' +
                '#alife-wp-widget .wp-item:hover .wp-acts{opacity:1;}' +
                '#alife-wp-widget .wp-act{border:1px solid rgba(255,255,255,.18);background:rgba(255,255,255,.07);color:#cfd3dd;font-size:11px;line-height:1;padding:5px 8px;border-radius:7px;cursor:pointer;white-space:nowrap;}' +
                '#alife-wp-widget .wp-act:hover{background:rgba(255,255,255,.18);color:#fff;}' +
                '#alife-wp-widget .wp-act.danger{color:#ffc9c9;border-color:rgba(255,120,120,.35);background:rgba(255,80,80,.12);}' +
                '#alife-wp-widget .wp-act.danger:hover{background:rgba(255,80,80,.28);}' +
                '#alife-wp-widget .wp-act.ok{color:#cfe6ff;border-color:rgba(124,196,255,.45);background:rgba(124,196,255,.16);}' +
                '#alife-wp-widget .wp-act.arm{color:#fff;border-color:rgba(255,90,90,.85);background:rgba(255,60,60,.55);font-weight:600;}' +
                // 全部 / 隐藏 / 回收站 的过滤条 + 数量对比
                '#alife-wp-widget .wp-subbar{display:flex;align-items:center;gap:6px;padding:0 2px 7px;flex-wrap:wrap;}' +
                '#alife-wp-widget .wp-f{border:1px solid rgba(255,255,255,.14);background:rgba(255,255,255,.05);color:#b9bcc8;font-size:11px;padding:4px 9px;border-radius:8px;cursor:pointer;white-space:nowrap;}' +
                '#alife-wp-widget .wp-f:hover{background:rgba(255,255,255,.12);}' +
                '#alife-wp-widget .wp-f.on{background:rgba(124,196,255,.24);border-color:rgba(124,196,255,.6);color:#e8f4ff;font-weight:600;}' +
                '#alife-wp-widget .wp-f em{font-style:normal;opacity:.75;margin-left:3px;}' +
                '#alife-wp-widget .wp-cmp{margin-left:auto;font-size:10px;color:#8b8b99;white-space:nowrap;}' +
                '#alife-wp-widget .wp-note{font-size:10px;color:#8b8b99;line-height:1.55;padding:0 3px 7px;}' +
                '#alife-wp-widget .wp-empty{padding:18px;text-align:center;color:#8b8b99;font-size:12px;line-height:1.7;}' +
                // 「我的」页的工具条与已添加目录
                '#alife-wp-widget .wp-mine-bar{display:flex;gap:6px;padding:2px 2px 6px;}' +
                '#alife-wp-widget .wp-mine-bar button{flex:1;border:1px solid rgba(124,196,255,.35);background:rgba(124,196,255,.14);color:#cfe6ff;font-size:11px;padding:5px 0;border-radius:8px;cursor:pointer;white-space:nowrap;}' +
                '#alife-wp-widget .wp-mine-bar button:hover{background:rgba(124,196,255,.26);}' +
                '#alife-wp-widget .wp-mine-bar button:disabled{opacity:.5;cursor:default;}' +
                '#alife-wp-widget .wp-dirs{display:flex;flex-direction:column;gap:2px;padding:0 2px 6px;}' +
                '#alife-wp-widget .wp-dir{display:flex;align-items:center;gap:6px;font-size:11px;color:#9aa0ae;background:rgba(255,255,255,.05);border-radius:7px;padding:3px 6px;}' +
                '#alife-wp-widget .wp-dir span{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}' +
                '#alife-wp-widget .wp-dir-x{border:0;background:transparent;color:#8b8b99;font-size:13px;line-height:1;cursor:pointer;padding:0 2px;}' +
                '#alife-wp-widget .wp-dir-x:hover{color:#ff9a9a;}';
              document.head.appendChild(css);
            }

            box = document.createElement('div');
            box.id = 'alife-wp-widget';
            box.innerHTML =
              '<div class="wp-panel">' +
                '<div class="wp-head"><b>壁纸</b><span class="wp-count"></span>' +
                  '<button class="wp-scrim" type="button" data-white="0">遮罩 黑</button>' +
                  '<button class="wp-close" type="button">&times;</button>' +
                '</div>' +
                '<div class="wp-sliders">' +
                  '<div class="wp-row"><span>画面</span><button class="wp-reset" type="button" title="遮罩 / 白度 / 模糊 / 亮度 / 对比 / 饱和 / 气泡 / 视差 全部回到默认">还原默认</button></div>' +
                  '<label>遮罩<input class="wp-op" type="range" min="0" max="80" step="5"><i></i></label>' +
                  '<label>白度<input class="wp-pa" type="range" min="0" max="100" step="5"><i></i></label>' +
                  '<label>模糊<input class="wp-bl" type="range" min="0" max="30" step="1"><i></i></label>' +
                  '<label>亮度<input class="wp-br" type="range" min="40" max="180" step="5"><i></i></label>' +
                  '<label>对比<input class="wp-ct" type="range" min="60" max="180" step="5"><i></i></label>' +
                  '<label>饱和<input class="wp-sa" type="range" min="0" max="200" step="5"><i></i></label>' +
                  '<label>气泡<input class="wp-bu" type="range" min="0" max="100" step="5"><i></i></label>' +
                  '<label>视差<input class="wp-px" type="range" min="0" max="100" step="5"><i></i></label>' +
                '</div>' +
                '<div class="wp-sliders wp-look">' +
                  '<div class="wp-row"><span>窗口</span><button class="wp-wpo-reset" type="button" title="面板不透明度回到默认 55">还原</button></div>' +
                  '<label title="只管这个设置窗口的黑底：0 = 全透明，直接看到身后的壁纸；100 = 纯黑面板。页面里那些卡片由「白度」控制，两者互不影响">面板<input class="wp-wpo" type="range" min="0" max="100" step="5"><i></i></label>' +
                '</div>' +
                '<div class="wp-sliders wp-sound">' +
                  '<div class="wp-row"><span>音轨</span><button class="wp-audio" type="button" data-on="0">关闭</button></div>' +
                  '<label title="100% = 原始音量；超过 100 走 WebAudio 增益放大，最高 200%">音量<input class="wp-vol" type="range" min="0" max="200" step="5"><i></i></label>' +
                  '<label>倍速<input class="wp-rate" type="range" min="50" max="200" step="10"><i></i></label>' +
                '</div>' +
                '<div class="wp-tabs">' +
                  '<button class="wp-tab on" type="button" data-view="list">壁纸</button>' +
                  '<button class="wp-tab" type="button" data-view="mine">我的</button>' +
                  '<button class="wp-tab" type="button" data-view="props">属性</button>' +
                '</div>' +
                '<div class="wp-list"></div>' +
                '<div class="wp-foot"><button class="wp-clear" type="button">清除壁纸</button>' +
                  '<button class="wp-home" type="button" title="把挂件挪回右下角">挂件归位</button></div>' +
              '</div>' +
              '<button class="wp-trigger" type="button" title="壁纸：切换 / 调透明度">' +
                '<img class="wp-pet-img" alt="" draggable="false">' +
                '<svg class="wp-pet-fallback" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round">' +
                  '<rect x="3" y="4" width="18" height="16" rx="2.5"/><circle cx="8.5" cy="9.5" r="1.6"/><path d="M21 16.2 15.6 10.8 7 20"/>' +
                '</svg><span class="wp-pet-fallback">壁纸</span>' +
              '</button>';

            // 内联样式兜底：即使页面样式表把我们压住/覆盖，按钮也一定可见
            box.style.cssText = 'position:fixed;right:18px;bottom:112px;z-index:2147483645;';

            var trigger = box.querySelector('.wp-trigger');
            var CLASSIC_CSS =
              'display:flex;align-items:center;gap:6px;height:38px;padding:0 14px 0 11px;border-radius:19px;' +
              'border:1px solid rgba(255,255,255,.28);background:rgba(22,22,28,.86);color:#eaeaef;' +
              'font:12px/1 system-ui,"Microsoft YaHei",sans-serif;cursor:pointer;' +
              'box-shadow:0 8px 22px rgba(0,0,0,.45);';
            trigger.style.cssText = CLASSIC_CSS;

            // 挂件形象：0 = 经典胶囊；1 / 2 = Q 版大肥鱼（贴图来自插件自己的静态服务）
            var petImg = trigger.querySelector('.wp-pet-img');
            var petPanel = box.querySelector('.wp-panel');

            var petPx = 76;   // 当前形象边长，面板定位要用

            // 面板默认贴着头像上方；挂件被拖到顶部放不下时改到下方
            function wpSyncPanelSide() {
              if (!petPanel) return;
              var r = box.getBoundingClientRect();
              var need = 520;
              var roomAbove = r.top;
              var roomBelow = window.innerHeight - r.bottom;
              var gap = (trigger.classList.contains('pet') ? petPx : 38) + 12;

              if (roomAbove < need && roomBelow > roomAbove) {
                petPanel.style.top = 'calc(100% + 12px)';
                petPanel.style.bottom = 'auto';
              } else {
                petPanel.style.top = '';
                petPanel.style.bottom = gap + 'px';
              }

              // 挂件拖到屏幕左半边时，面板改从它右边长出去，别伸到屏幕外
              var pw = 392;
              if (r.left < pw + 8 && window.innerWidth - r.right > pw * 0.5) {
                petPanel.style.left = '0';
                petPanel.style.right = 'auto';
              } else {
                petPanel.style.left = '';
                petPanel.style.right = '0';
              }
            }

            function wpSetPet(avatar, size) {
              avatar = Math.round(+avatar || 0);
              var px = Math.max(56, Math.min(120, Math.round(+size || 76)));

              if (avatar <= 0) {
                trigger.classList.remove('pet');
                trigger.classList.remove('failed');
                trigger.style.cssText = CLASSIC_CSS;
                wpSyncPanelSide();
                return;
              }

              petPx = px;

              trigger.classList.add('pet');
              trigger.style.cssText =
                'width:' + px + 'px;height:' + px + 'px;padding:0;border:0;background:transparent;' +
                'box-shadow:none;backdrop-filter:none;-webkit-backdrop-filter:none;cursor:pointer;' +
                'display:flex;align-items:center;justify-content:center;transition:transform .16s ease;';
              wpSyncPanelSide();

              // __alifeWpApi 自带尾斜杠，直接拼会变成 //whale/…
              var base = (window.__alifeWpApi || '').replace(/\/+$/, '');
              var src = base + (avatar === 2 ? '/whale/pet-bubble.png' : '/whale/pet-face.png');
              if (petImg.getAttribute('src') !== src) {
                petImg.onerror = function () { trigger.classList.add('failed'); };
                petImg.onload = function () { trigger.classList.remove('failed'); };
                petImg.setAttribute('src', src);
              }
            }

            // 设置面板自己的不透明度（a = 0~1）：跟页面卡片的「白度」完全分开
            // 0 时连磨砂一起关掉，否则背后壁纸还是被糊住，看着像没生效
            function wpApplyPanelLook(a) {
              if (!petPanel) return;
              a = +a;
              if (!isFinite(a)) a = 0.55;
              a = Math.max(0, Math.min(1, a));

              petPanel.style.backgroundColor = 'rgba(18,18,23,' + a.toFixed(3) + ')';
              petPanel.style.backdropFilter = a > 0.005 ? 'blur(' + Math.round(16 * a) + 'px)' : 'none';
              petPanel.style.webkitBackdropFilter = a > 0.005 ? 'blur(' + Math.round(16 * a) + 'px)' : 'none';
              // 太透的时候内容要自己撑住可读性（描边 + 浅底）
              petPanel.classList.toggle('wp-thin', a < 0.45);
            }

            // 设置面板改完形象后由 __alifeWallpaperRestyle 调它即时生效
            window.__alifeWpPet = wpSetPet;
            window.__alifeWpPanelLook = wpApplyPanelLook;

            document.body.appendChild(box);

            // 创建时就按当前配置定形象：面板没被打开过也要是 Q 版，不能等第一次 refresh
            try { wpSetPet(petAvatar === undefined ? 1 : petAvatar, petSize); } catch (e) { }
            // 面板底色同理：不必等第一次 refresh，打开就该是配置里的那个透明度
            try { wpApplyPanelLook(wpanel === undefined ? 0.55 : wpanel); } catch (e) { }

            var list = box.querySelector('.wp-list');
            var countEl = box.querySelector('.wp-count');
            var sliders = {
              op: box.querySelector('.wp-op'),
              pa: box.querySelector('.wp-pa'),
              bl: box.querySelector('.wp-bl'),
              br: box.querySelector('.wp-br'),
              ct: box.querySelector('.wp-ct'),
              sa: box.querySelector('.wp-sa'),
              bu: box.querySelector('.wp-bu'),
              px: box.querySelector('.wp-px'),
              wpo: box.querySelector('.wp-wpo'),
              vol: box.querySelector('.wp-vol'),
              rate: box.querySelector('.wp-rate')
            };
            var scrimBtn = box.querySelector('.wp-scrim');
            var audioBtn = box.querySelector('.wp-audio');
            var tabsEl = box.querySelector('.wp-tabs');
            var tabList = box.querySelector('.wp-tab[data-view="list"]');
            var tabProps = box.querySelector('.wp-tab[data-view="props"]');
            var tabMine = box.querySelector('.wp-tab[data-view="mine"]');

            // 场景壁纸的用户属性（project.json 的 general.properties）
            var propsOpen = false;
            // 「我的」标签：只看自己导入的图片 / 视频，不和引擎壁纸混在一张列表里
            var mineOpen = false;
            // 列表内的过滤视图：all = 正常列表，hidden = 隐藏清单，trash = 回收站
            var view = 'all';
            var propsCache = [];
            var lastState = null;      // 最近一次 state，属性页要拿它判断「这次改动到底落到哪了」

            // 当前这张壁纸有没有音轨（点了「开启」之后才判定；没有就把音轨/音量置灰）
            var noAudio = false;
            var lastCurId = null;

            function esc(text) {
              return String(text == null ? '' : text).replace(/[&<>"']/g, function (c) {
                return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
              });
            }

            function call(path) {
              return fetch((window.__alifeWpApi || '') + path, { cache: 'no-store' })
                .then(function (r) { return r.json(); })
                .catch(function () { return null; });
            }

            function wpClip(text, max) {
              var s = String(text == null ? '' : text).trim();
              return s.length > max ? s.slice(0, max) + '…' : s;
            }

            // 倍速按倍数显示：1.0x / 1.5x / 2.0x，别甩 110、120 这种百分比
            function fmtRate(v) {
              var n = +v / 100;
              if (!isFinite(n)) n = 1;
              return n.toFixed(1) + 'x';
            }

            function setSlider(el, v, fmt) {
              if (!el || v == null) return;
              el.value = v;
              var out = el.parentNode.querySelector('i');
              if (out) out.textContent = fmt ? fmt(v) : v;
            }

            // 颜色在 WE 里是 "r g b" 的 0..1 浮点三元组，控件用 #rrggbb
            function colorToHex(v) {
              var p = String(v == null ? '' : v).trim().split(/\s+/);
              if (p.length < 3) return '#ffffff';

              var out = '#';
              for (var i = 0; i < 3; i++) {
                var n = Math.max(0, Math.min(1, parseFloat(p[i]) || 0));
                out += ('0' + Math.round(n * 255).toString(16)).slice(-2);
              }
              return out;
            }

            function hexToColor(hex) {
              var s = String(hex || '#ffffff').replace('#', '');
              var r = parseInt(s.slice(0, 2), 16) / 255;
              var g = parseInt(s.slice(2, 4), 16) / 255;
              var b = parseInt(s.slice(4, 6), 16) / 255;
              return r.toFixed(3) + ' ' + g.toFixed(3) + ' ' + b.toFixed(3);
            }

            function findProp(name) {
              for (var i = 0; i < propsCache.length; i++) if (propsCache[i].name === name) return propsCache[i];
              return null;
            }

            // 挂件列表右下角那行小字：图片 / 视频是用户自己丢进来的，别显示成英文 type
            function wpTypeLabel(it, state) {
              if (it.type === 'video') return '视频';
              if (it.type === 'image') return '图片';
              if (it.type === 'web') return '网页';
              if (it.playable) return it.type;
              if (it.type === 'scene') return (state && state.liveScene) ? '场景' : '仅预览';
              return '仅预览';
            }

            // 隐藏 / 回收站里的条目只有类型字符串，没有 state 上下文
            function wpTypeText(type) {
              if (type === 'video') return '视频';
              if (type === 'image') return '图片';
              if (type === 'web') return '网页';
              if (type === 'scene') return '场景';
              return type || '壁纸';
            }

            function sendProp(name, value, commit) {
              var url = 'api/prop?name=' + encodeURIComponent(name) + '&value=' + encodeURIComponent(JSON.stringify(value));

              call(url).then(function (st) {
                if (!st || !st.ok) return;
                propsCache = st.props || propsCache;
                lastState = st;
                if (commit) render(st);   // 松开后再重画一次，条件显隐跟着更新
              });
            }

            // 属性值到底有没有落到画面上：引擎侧没有 scene 目标 / 场景脚本关着，都只保存不改画面
            function propsWarning(state) {
              if (!state || state.kind !== 'scene') return '';
              if (!state.liveScene) return '这张壁纸当前只有预览图（实时渲染未开），改数值看不到变化。';
              if (state.scene === 'down') return '实时渲染没跑起来，现在显示的是静态预览图（窗口在后台时挂载会超时）。切回前台会自动重挂，数值也会补上。';
              if (state.scripts === false) return '场景脚本已关闭：数值会记住，但壁纸读不到它。到模块设置里打开「运行场景脚本」后重新切换这张壁纸。';

              var applied = String(state.applied || '');
              if (applied.indexOf('no-scene') >= 0) return '场景还在加载（或渲染失败退回了预览图），数值先记下，等场景起来会自动带上。';
              if (applied.indexOf('err') >= 0) return '引擎拒绝了这次改动：' + applied;
              if (applied.indexOf('no-native') >= 0) return '这个场景引擎没有原生属性通道（' + applied + '），颜色 / 大小 / 位置可能不会变。';

              return '';
            }

            function resetBtn(p) {
              return '<button type="button" class="wp-prop-reset' + (p.overridden ? '' : ' is-def') + '"' +
                     ' data-reset="' + esc(p.name) + '" title="' + (p.overridden ? '还原成作者默认值' : '已经是默认值') + '">↺</button>';
            }

            function renderProps() {
              var out = '<div class="wp-hint">改动立即生效，并按这张壁纸记住</div>';

              var warn = propsWarning(lastState);
              if (warn) out += '<div class="wp-warn">' + esc(warn) + '</div>';

              if (lastState && lastState.scene === 'down')
                out += '<div class="wp-prop-actions"><span>实时渲染已停（只剩预览图）</span>' +
                         '<button type="button" class="wp-remount">重新挂载</button></div>';

              out += '<div class="wp-prop-actions"><span>↺ 单个还原 · 全部还原</span>' +
                       '<button type="button" class="wp-prop-resetall">全部还原</button></div>';

              for (var i = 0; i < propsCache.length; i++) {
                var p = propsCache[i];
                if (p.hidden) continue;

                if (p.kind === 'title') {
                  // 作者的分组标题里常带整段 BBCode/路径，截断一下，别让它占满整屏
                  out += '<div class="wp-prop-title">' + esc(wpClip(p.text, 180)) + '</div>';
                  continue;
                }

                if (p.kind === 'bool') {
                  var on = p.value === true || p.value === 'true';
                  out += '<div class="wp-prop-row"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                           '<button type="button" class="wp-prop-bool' + (on ? ' on' : '') + '" data-prop="' + esc(p.name) + '" data-on="' + (on ? '1' : '0') + '">' + (on ? '开' : '关') + '</button>' +
                           resetBtn(p) +
                         '</div>';
                  continue;
                }

                if (p.kind === 'slider') {
                  var min = p.min == null ? 0 : p.min;
                  var max = p.max == null ? 100 : p.max;
                  var step = p.step == null ? 0.01 : p.step;
                  var val = p.value == null ? min : p.value;

                  out += '<div class="wp-prop-slider"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                           '<i>' + val + '</i>' + resetBtn(p) +
                           '<input type="range" data-prop="' + esc(p.name) + '" data-kind="slider" min="' + min + '" max="' + max + '" step="' + step + '" value="' + val + '">' +
                         '</div>';
                  continue;
                }

                if (p.kind === 'combo') {
                  out += '<div class="wp-prop-line"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                           '<select data-prop="' + esc(p.name) + '" data-kind="combo">';
                  var opts = p.options || [];
                  for (var k = 0; k < opts.length; k++) {
                    out += '<option value="' + k + '"' + (String(opts[k].value) === String(p.value) ? ' selected' : '') + '>' + esc(opts[k].label) + '</option>';
                  }
                  out += '</select>' + resetBtn(p) + '</div>';
                  continue;
                }

                if (p.kind === 'color') {
                  out += '<div class="wp-prop-line"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                           '<input type="color" data-prop="' + esc(p.name) + '" data-kind="color" value="' + colorToHex(p.value) + '">' +
                           resetBtn(p) +
                         '</div>';
                  continue;
                }

                if (p.kind === 'file' || p.kind === 'directory') {
                  out += '<div class="wp-prop-line"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                           '<select data-prop="' + esc(p.name) + '" data-kind="text"><option value="">（默认）</option>';
                  var files = p.files || [];
                  for (var f = 0; f < files.length; f++) {
                    out += '<option value="' + esc(files[f]) + '"' + (files[f] === p.value ? ' selected' : '') + '>' + esc(files[f]) + '</option>';
                  }
                  out += '</select>' + resetBtn(p) + '</div>';
                  continue;
                }

                out += '<div class="wp-prop-line"><span title="' + esc(p.name) + '">' + esc(p.text) + '</span>' +
                         '<input type="text" data-prop="' + esc(p.name) + '" data-kind="text" value="' + esc(p.value == null ? '' : p.value) + '">' +
                         resetBtn(p) +
                       '</div>';
              }

              list.innerHTML = out || '<div class="wp-empty">这张壁纸没有可调属性</div>';
            }

            function render(state) {
              if (!state || !state.ok) {
                // 列表已经有内容就别擦掉：一次瞬时失败不该把壁纸列表清空
                if (!list.children.length) list.innerHTML = '<div class="wp-empty">读取壁纸库失败，收起再点开重试</div>';
                return;
              }

              lastState = state;

              // 位置以「最后一次拖动」为准：面板开合 / 重绘都不该把挂件弹回去
              // 正在拖动时绝不动它，否则异步回来的这份 state 会把半路拖到一半的挂件拽回旧坐标
              if (window.__alifeWpPos && !wpDrag) {
                try { wpPlaceAt(window.__alifeWpPos.x, window.__alifeWpPos.y); } catch (e) { }
              }

              setSlider(sliders.op, state.opacity);
              setSlider(sliders.pa, state.panel);
              setSlider(sliders.bl, state.blur);
              setSlider(sliders.br, state.brightness);
              setSlider(sliders.ct, state.contrast);
              setSlider(sliders.sa, state.saturate);
              setSlider(sliders.bu, state.bubble);
              setSlider(sliders.px, state.parallax);
              // 面板不透明度：服务端给的是 0~1，滑块用 0~100
              setSlider(sliders.wpo, state.wpanel == null ? 55 : Math.round(state.wpanel * 100));
              try { wpApplyPanelLook(state.wpanel); } catch (e) { }
              setSlider(sliders.vol, state.volume);
              setSlider(sliders.rate, state.rate, fmtRate);

              // 挂件形象（设置面板里改完下一次轮询就生效，不用重开页面）
              try { wpSetPet(state.petAvatar, state.petSize); } catch (e) { }

              var curId = '';
              for (var c0 = 0; c0 < state.items.length; c0++) {
                if (state.items[c0].current) { curId = state.items[c0].id; break; }
              }
              if (curId !== lastCurId) {
                lastCurId = curId;
                noAudio = false;
              }

              // 音轨 / 音量 / 倍速只有视频壁纸有意义：场景壁纸只有一张封面，网页壁纸在 iframe 里够不到
              var isVideo = state.kind === 'video';

              if (audioBtn) {
                audioBtn.setAttribute('data-on', state.mute ? '0' : '1');
                audioBtn.textContent = noAudio ? '无音轨' : (state.mute ? '关闭' : '开启');
                audioBtn.className = (state.mute || noAudio) ? 'wp-audio' : 'wp-audio on';
                audioBtn.disabled = !isVideo;
                audioBtn.title = !isVideo ? '只有视频壁纸有声' : (noAudio ? '这段壁纸本身没有音轨，静音 / 音量无效' : '');
              }

              if (sliders.vol) {
                sliders.vol.disabled = !isVideo || noAudio;
                sliders.vol.title = !isVideo ? '只有视频壁纸可调' : (noAudio ? '这段壁纸没有音轨' : '');
              }

              if (sliders.rate) {
                sliders.rate.disabled = !isVideo;
                sliders.rate.title = isVideo ? '' : '只有视频壁纸可调';
              }

              if (scrimBtn) {
                scrimBtn.setAttribute('data-white', state.whiteScrim ? '1' : '0');
                scrimBtn.textContent = state.whiteScrim ? '遮罩 白' : '遮罩 黑';
              }

              countEl.textContent = state.count + ' 张';

              propsCache = state.props || [];

              var propCount = 0;
              for (var pc = 0; pc < propsCache.length; pc++) {
                if (!propsCache[pc].hidden && propsCache[pc].kind !== 'title') propCount++;
              }

              // 换了壁纸之后没有可调项了：自动退回列表，别卡在空属性页
              if (!propCount && propsOpen) propsOpen = false;

              if (tabsEl) tabsEl.style.display = 'flex';

              // 两套列表分开：引擎壁纸（创意工坊 / 项目）和自己导入的图片视频
              var mineItems = [], engineItems = [];
              for (var si = 0; si < state.items.length; si++) {
                (state.items[si].src === 'media' ? mineItems : engineItems).push(state.items[si]);
              }

              // 标题上的数字跟着当前标签走：壁纸 = 引擎壁纸数，我的 = 自己的素材数
              if (countEl) countEl.textContent = (mineOpen ? mineItems.length : engineItems.length) + ' 张';

              if (tabProps) {
                tabProps.className = 'wp-tab' + (propsOpen ? ' on' : '');
                tabProps.innerHTML = '属性 <em>' + propCount + '</em>';
              }

              if (tabMine) {
                tabMine.className = 'wp-tab' + (!propsOpen && mineOpen ? ' on' : '');
                tabMine.innerHTML = '我的 <em>' + mineItems.length + '</em>';
              }

              if (tabList) tabList.className = 'wp-tab' + (!propsOpen && !mineOpen ? ' on' : '');

              if (propsOpen) { renderProps(); return; }

              var shown = mineOpen ? mineItems : engineItems;
              var hiddenList = state.hidden || [];
              var trashList = state.trash || [];
              var html = '';

              // 过滤条：全部 / 隐藏 / 回收站；右边是「扫描到多少 · 列表里多少」的对比
              html += '<div class="wp-subbar">' +
                        '<button class="wp-f' + (view === 'all' ? ' on' : '') + '" type="button" data-f="all">全部<em>' + shown.length + '</em></button>' +
                        '<button class="wp-f' + (view === 'hidden' ? ' on' : '') + '" type="button" data-f="hidden">隐藏<em>' + hiddenList.length + '</em></button>' +
                        '<button class="wp-f' + (view === 'trash' ? ' on' : '') + '" type="button" data-f="trash">回收站<em>' + trashList.length + '</em></button>' +
                        // 对比只在当前标签内算：壁纸页比引擎壁纸，我的页比自己的素材
                        '<span class="wp-cmp" title="左 = 这次扫描到的条数（含被排除词 / 跳过场景过滤掉的）；右 = 当前标签里显示的条数">扫描 ' +
                          (mineOpen ? (state.scannedMedia || 0) : (state.scannedEngine || 0)) + ' · 显示 ' + shown.length +
                        '</span>' +
                      '</div>';

              if (view === 'all' && mineOpen) {
                html += '<div class="wp-mine-bar">' +
                          '<button class="wp-adddir" type="button" title="选一个文件夹，递归扫描里面的图片和视频">＋ 文件夹</button>' +
                          '<button class="wp-rescan" type="button" title="重新扫描；新丢进去的文件也会出来">重扫</button>' +
                          '<button class="wp-reveal" type="button" title="在资源管理器里打开推荐投放目录">打开目录</button>' +
                          // 我的素材的图片 / 视频数量对比
                          '<span class="wp-cmp" title="扫描到的素材里，图片和视频各有多少">图片 ' + (state.imageCount || 0) + ' · 视频 ' + (state.videoCount || 0) + '</span>' +
                        '</div>';

                var dirs = state.mediaDirs || [];
                if (dirs.length) {
                  html += '<div class="wp-dirs">';
                  for (var di = 0; di < dirs.length; di++) {
                    html += '<div class="wp-dir" title="' + esc(dirs[di]) + '">' +
                              '<span>' + esc(shortDir(dirs[di])) + '</span>' +
                              '<button class="wp-dir-x" type="button" data-dir="' + esc(dirs[di]) + '" title="不再扫描这个目录">×</button>' +
                            '</div>';
                  }
                  html += '</div>';
                }
              }

              if (view === 'trash') {
                html += '<div class="wp-note">删除的壁纸已经搬到插件的 Recycle 目录（不会自动重新加载）。' +
                        '逐项点「还原」会搬回原位置；「彻底删除」才会真的删掉。</div>';

                for (var ti = 0; ti < trashList.length; ti++) {
                  var tr = trashList[ti];
                  html += '<div class="wp-item">' +
                            '<span class="wp-pick aside" title="' + esc(tr.key) + '">' +
                              '<span class="wp-noimg"></span>' +
                              '<span class="wp-col">' +
                                '<span class="wp-title">' + esc(tr.title || tr.key) + '</span>' +
                                '<em class="wp-type">' + (tr.moved === false ? '文件未搬移 · ' : '') + esc(tr.at || '') + '</em>' +
                              '</span>' +
                            '</span>' +
                            '<span class="wp-acts">' +
                              '<button class="wp-act ok wp-restore" type="button" data-key="' + esc(tr.key) + '" title="搬回原位置，只还原这一条">还原</button>' +
                              '<button class="wp-act danger wp-purge" type="button" data-key="' + esc(tr.key) + '" title="真删文件，不能撤销">彻底删除</button>' +
                            '</span>' +
                          '</div>';
                }

                if (!trashList.length) html += '<div class="wp-empty">回收站是空的</div>';
              } else if (view === 'hidden') {
                html += '<div class="wp-note">隐藏只登记、不动文件，Wallpaper Engine 缓存的壁纸也能藏。点「恢复」就回到列表。</div>';

                for (var hi = 0; hi < hiddenList.length; hi++) {
                  var hn = hiddenList[hi];
                  html += '<div class="wp-item">' +
                            '<span class="wp-pick aside" title="' + esc(hn.key) + '">' +
                              '<span class="wp-noimg"></span>' +
                              '<span class="wp-col">' +
                                '<span class="wp-title">' + esc(hn.title || hn.key) + '</span>' +
                                '<em class="wp-type">' + esc(wpTypeText(hn.type)) + (hn.at ? ' · ' + esc(hn.at) : '') + '</em>' +
                              '</span>' +
                            '</span>' +
                            '<span class="wp-acts">' +
                              '<button class="wp-act ok wp-unhide" type="button" data-key="' + esc(hn.key) + '" title="取消隐藏，回到列表">恢复</button>' +
                            '</span>' +
                          '</div>';
                }

                if (!hiddenList.length) html += '<div class="wp-empty">没有隐藏的壁纸</div>';
              } else {
                for (var i = 0; i < shown.length; i++) {
                  var it = shown[i];
                  html += '<div class="wp-item' + (it.current ? ' current' : '') + '">' +
                            '<button class="wp-pick" type="button" data-id="' + esc(it.id) + '" title="' + esc(it.title) + '">' +
                              (it.preview ? '<img src="' + esc(it.preview) + '" loading="lazy" alt="">' : '<span class="wp-noimg"></span>') +
                              '<span class="wp-col">' +
                                '<span class="wp-title">' + esc(it.title) + '</span>' +
                                '<em class="wp-type">' + wpTypeLabel(it, state) + '</em>' +
                              '</span>' +
                            '</button>' +
                            '<span class="wp-acts">' +
                              '<button class="wp-act wp-hide" type="button" data-id="' + esc(it.id) + '" title="隐藏：从列表里收起来，文件不动">隐藏</button>' +
                              '<button class="wp-act danger wp-del" type="button" data-id="' + esc(it.id) + '" title="删除：搬到回收站，可以还原">删除</button>' +
                            '</span>' +
                          '</div>';
                }

                if (!shown.length) {
                  html += mineOpen
                    ? '<div class="wp-empty">还没有自己的素材<br><span style="font-size:11px;opacity:.75">点上面「＋ 文件夹」选一个目录，递归扫描其中的图片和视频</span></div>'
                    : '<div class="wp-empty">壁纸库是空的<br><span style="font-size:11px;opacity:.75">订阅 Wallpaper Engine 壁纸，或到「我的」里添加文件夹</span></div>';
                }
              }

              list.innerHTML = html;
            }

            // 目录太长只显示末两段，完整路径放 title 里
            function shortDir(path) {
              var parts = String(path || '').split(/[\\/]+/).filter(function (x) { return x.length; });
              if (parts.length <= 2) return parts.join('\\');
              return parts.slice(-2).join('\\');
            }

            function refresh() { return call('api/state').then(render); }

            var timer = 0;
            function pushConfig() {
              clearTimeout(timer);
              timer = setTimeout(function () {
                call('api/config' +
                  '?opacity=' + sliders.op.value +
                  '&panel=' + sliders.pa.value +
                  '&blur=' + sliders.bl.value +
                  '&br=' + sliders.br.value +
                  '&ct=' + sliders.ct.value +
                  '&sa=' + sliders.sa.value +
                  '&bu=' + sliders.bu.value +
                  '&px=' + sliders.px.value +
                  // 挂件面板自己的不透明度（单独一项，跟 panel / 白度 不冲突）
                  '&wpo=' + (sliders.wpo ? sliders.wpo.value : 55)).then(render);
              }, 160);
            }

            // 音量 / 倍速只走媒体参数，不重建壁纸层
            var mediaTimer = 0;
            function pushMedia() {
              clearTimeout(mediaTimer);
              mediaTimer = setTimeout(function () {
                // 注意：第一个参数必须用 ? 开头，写成 api/config&vol= 会变成未知接口，
                // 面板会拿到 ok:false 并把壁纸列表刷成“读取壁纸库失败”。
                var q = 'api/config?vol=' + (sliders.vol ? sliders.vol.value : 60);
                if (sliders.rate) q += '&rate=' + sliders.rate.value;
                call(q).then(render);
              }, 160);
            }

            [sliders.vol, sliders.rate].forEach(function (el) {
              if (!el) return;
              el.addEventListener('input', function () {
                var out = el.parentNode.querySelector('i');
                if (out) out.textContent = el === sliders.rate ? fmtRate(el.value) : el.value;
                pushMedia();
              });
            });

            [sliders.op, sliders.pa, sliders.bl, sliders.br, sliders.ct, sliders.sa, sliders.bu, sliders.px, sliders.wpo].forEach(function (el) {
              if (!el) return;
              el.addEventListener('input', function () {
                var out = el.parentNode.querySelector('i');
                if (out) out.textContent = el.value;
                pushConfig();
              });
            });

            // 逐项二次确认：第一次点把按钮变成「确认…」，再点一次才真的执行。
            // 目的就是不让「恢复 / 彻底删除」变成一键批量操作。
            function wpArm(btn, label, run) {
              var now = Date.now();
              var armedAt = parseInt(btn.getAttribute('data-arm-at') || '0', 10);

              if (btn.getAttribute('data-arm') === '1' && now - armedAt < 6000) {
                btn.removeAttribute('data-arm');
                btn.textContent = '处理中…';
                btn.disabled = true;
                run();
                return;
              }

              btn.setAttribute('data-arm', '1');
              btn.setAttribute('data-arm-at', String(now));

              var original = btn.textContent;
              btn.textContent = label;
              btn.classList.add('arm');

              setTimeout(function () {
                if (btn.getAttribute('data-arm') === '1') {
                  btn.removeAttribute('data-arm');
                  btn.classList.remove('arm');
                  btn.textContent = original;
                }
              }, 6000);
            }

            list.addEventListener('click', function (ev) {
              var t0 = ev.target;
              if (!t0 || !t0.closest) return;

              // 全部 / 隐藏 / 回收站 三个视图切换
              var f = t0.closest('.wp-f');
              if (f) {
                var next = f.getAttribute('data-f') || 'all';
                if (next === view) return;
                view = next;
                if (lastState) render(lastState);
                return;
              }

              // 隐藏：不弹确认，随时能恢复
              var hb = t0.closest('.wp-hide');
              if (hb) {
                hb.disabled = true;
                call('api/hide?id=' + encodeURIComponent(hb.getAttribute('data-id'))).then(render);
                return;
              }

              // 删除：搬到回收站，要确认一次
              var db = t0.closest('.wp-del');
              if (db) {
                wpArm(db, '确认删除?', function () {
                  call('api/delete?id=' + encodeURIComponent(db.getAttribute('data-id'))).then(render);
                });
                return;
              }

              // 隐藏项恢复
              var ub = t0.closest('.wp-unhide');
              if (ub) {
                wpArm(ub, '确认恢复?', function () {
                  call('api/unhide?key=' + encodeURIComponent(ub.getAttribute('data-key'))).then(render);
                });
                return;
              }

              // 回收站：还原单条
              var rb = t0.closest('.wp-restore');
              if (rb) {
                wpArm(rb, '确认还原?', function () {
                  call('api/restore?key=' + encodeURIComponent(rb.getAttribute('data-key'))).then(render);
                });
                return;
              }

              // 回收站：彻底删除（真删文件）
              var pb = t0.closest('.wp-purge');
              if (pb) {
                wpArm(pb, '确认彻底删除?', function () {
                  call('api/purge?key=' + encodeURIComponent(pb.getAttribute('data-key'))).then(render);
                });
                return;
              }

              // 「我的」页的工具条：加文件夹 / 重扫 / 打开目录 / 移除某个目录
              if (t0.closest('.wp-adddir')) {
                t0.closest('.wp-adddir').disabled = true;
                call('api/pickdir').then(render);
                return;
              }

              if (t0.closest('.wp-rescan')) {
                call('api/rescan').then(render);
                return;
              }

              if (t0.closest('.wp-reveal')) {
                call('api/reveal').then(render);
                return;
              }

              var x = t0.closest('.wp-dir-x');
              if (x) {
                call('api/forgetdir?path=' + encodeURIComponent(x.getAttribute('data-dir'))).then(render);
                return;
              }

              // 只有正文区域（缩略图 + 标题）才切壁纸；右侧的动作按钮不能触发切换
              var pick = t0.closest('.wp-pick[data-id]');
              if (!pick) return;

              call('api/switch?id=' + encodeURIComponent(pick.getAttribute('data-id'))).then(render);
            });

            // 面板不透明度单独还原（WallpaperConfig.WidgetPanelOpacity 默认 55）
            var wpoResetBtn = box.querySelector('.wp-wpo-reset');
            if (wpoResetBtn && sliders.wpo) {
              wpoResetBtn.addEventListener('click', function () {
                setSlider(sliders.wpo, 55);
                pushConfig();
              });
            }

            // 画面参数还原默认（与 WallpaperConfig 里的默认值一致）
            var resetCfgBtn = box.querySelector('.wp-reset');
            if (resetCfgBtn) {
              resetCfgBtn.addEventListener('click', function () {
                var defaults = { op: 35, pa: 45, bl: 0, br: 100, ct: 100, sa: 100, bu: 65, px: 35 };
                for (var key in defaults) {
                  var el = sliders[key];
                  if (!el) continue;
                  el.value = defaults[key];
                  var out = el.parentNode.querySelector('i');
                  if (out) out.textContent = defaults[key];
                }
                pushConfig();
              });
            }

            if (tabList) {
              tabList.addEventListener('click', function () {
                if (!propsOpen && !mineOpen && view === 'all') return;
                propsOpen = false;
                mineOpen = false;
                view = 'all';
                refresh();
              });
            }

            // 我的：自己导入的图片 / 视频单独一页
            if (tabMine) {
              tabMine.addEventListener('click', function () {
                if (mineOpen && view === 'all') return;
                propsOpen = false;
                mineOpen = true;
                view = 'all';
                refresh();
              });
            }

            if (tabProps) {
              tabProps.addEventListener('click', function () {
                if (propsOpen) return;
                propsOpen = true;
                mineOpen = false;
                view = 'all';
                refresh();
              });
            }

            // 属性视图：控件是每次重画的，事件统一挂在容器上
            list.addEventListener('click', function (ev) {
              var t = ev.target;
              if (!t || !t.closest) return;

              var b = t.closest('.wp-prop-bool');
              if (b) {
                sendProp(b.getAttribute('data-prop'), b.getAttribute('data-on') !== '1', true);
                return;
              }

              // 单个属性还原：服务端会连作者默认值一起推回场景，不只是删存档
              var r = t.closest('.wp-prop-reset');
              if (r) {
                call('api/prop?reset=1&name=' + encodeURIComponent(r.getAttribute('data-reset'))).then(function (st) {
                  if (st && st.ok) render(st);
                });
                return;
              }

              // 整张壁纸全部还原
              if (t.closest('.wp-prop-resetall')) {
                call('api/prop?reset=1').then(function (st) { if (st && st.ok) render(st); });
                return;
              }

              // 实时渲染掉了：一键重挂（pkg 200MB+，首帧要十几秒，挂完再刷新一次面板）
              if (t.closest('.wp-remount')) {
                call('api/remount').then(function () {
                  setTimeout(function () { call('api/state').then(render); }, 16000);
                });
              }
            });

            list.addEventListener('input', function (ev) {
              var el = ev.target;
              if (!el || !el.getAttribute || el.getAttribute('data-kind') !== 'slider') return;

              var out = el.parentNode.querySelector('i');
              if (out) out.textContent = el.value;

              sendProp(el.getAttribute('data-prop'), parseFloat(el.value), false);
            });

            list.addEventListener('change', function (ev) {
              var el = ev.target;
              if (!el || !el.getAttribute) return;

              var name = el.getAttribute('data-prop');
              if (!name) return;

              var kind = el.getAttribute('data-kind');

              if (kind === 'slider') { sendProp(name, parseFloat(el.value), true); return; }

              if (kind === 'combo') {
                var def = findProp(name);
                var idx = parseInt(el.value, 10) || 0;
                var opt = def && def.options ? def.options[idx] : null;
                sendProp(name, opt ? opt.value : el.value, true);
                return;
              }

              if (kind === 'color') { sendProp(name, hexToColor(el.value), true); return; }

              sendProp(name, el.value, true);
            });

            // ---- 挂件拖动摆放 / 双击归位 ----
            // 松手才落盘：拖动过程每帧写配置会把存储打满
            var wpDrag = null;
            var wpSuppressClick = false;
            var wpDragAt = -999999;   // 最近一次成功拖动的时刻，用来压掉紧随其后的双击

            // 拖动诊断（页面里 __alifeWpDragLog 可读，/api/eval 排查用）
            function wpLog(msg) {
              try {
                var log = window.__alifeWpDragLog || (window.__alifeWpDragLog = []);
                log.push(new Date().toISOString().slice(11, 19) + ' ' + msg);
                if (log.length > 40) log.shift();
              } catch (e) { }
            }

            function wpClampPos(x, y) {
              var r = box.getBoundingClientRect();
              var maxX = Math.max(4, window.innerWidth - r.width - 4);
              var maxY = Math.max(4, window.innerHeight - r.height - 4);
              return { x: Math.max(4, Math.min(maxX, x)), y: Math.max(4, Math.min(maxY, y)) };
            }

            function wpPlaceAt(x, y) {
              var p = wpClampPos(x, y);
              box.style.left = Math.round(p.x) + 'px';
              box.style.top = Math.round(p.y) + 'px';
              box.style.right = 'auto';
              box.style.bottom = 'auto';
            }

            function wpPlaceDefault() {
              box.style.left = '';
              box.style.top = '';
              box.style.right = '18px';
              box.style.bottom = '112px';
            }

            function wpSavePos(x, y) {
              // 页面内也记一份：万一挂件 DOM 被重建，不会又回到右下角
              if (x < 0 || y < 0) {
                window.__alifeWpPos = null;
                // 归位要连会话缓存一起清，否则下次重建又被抬回旧坐标（服务端已是 -1，两边会打架）
                try { sessionStorage.removeItem('alifeWpPos'); } catch (e) { }
              } else {
                window.__alifeWpPos = { x: Math.round(x), y: Math.round(y) };
                // 页面导航会清掉 window，sessionStorage 兜一层，重开面板也能坐回原处
                try { sessionStorage.setItem('alifeWpPos', JSON.stringify(window.__alifeWpPos)); } catch (e) { }
              }
              wpLog('save ' + Math.round(x) + ',' + Math.round(y));
              call('api/move?x=' + Math.round(x) + '&y=' + Math.round(y)).then(function (r) {
                wpLog('save-result ' + JSON.stringify(r));
              });
            }

            // 图片原生拖拽一启动，浏览器就不再派发 mousemove / mouseup，拖动会直接断掉
            trigger.addEventListener('dragstart', function (e) { e.preventDefault(); });

            trigger.addEventListener('mousedown', function (e) {
              if (e.button !== 0) return;
              var r = box.getBoundingClientRect();
              wpDrag = { x0: e.clientX, y0: e.clientY, left0: r.left, top0: r.top, moved: false };
              wpLog('down ' + Math.round(r.left) + ',' + Math.round(r.top));
              e.preventDefault();   // 别让浏览器把按下当成选字 / 原生图片拖拽
            });

            // 捕获阶段监听：页面里任何一层 stopPropagation 都挡不住。
            // 之前挂在 document 冒泡上，被上层截胡就一个 mousemove 都收不到，于是永远进不了拖动分支。
            window.addEventListener('mousemove', function (e) {
              window.__wgwMoveSeen = (window.__wgwMoveSeen || 0) + 1;
              if (!wpDrag) return;
              var dx = e.clientX - wpDrag.x0;
              var dy = e.clientY - wpDrag.y0;
              if (!wpDrag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;   // 4px 阈值：别把单击吃掉
              if (!wpDrag.moved) wpLog('drag-start');
              wpDrag.moved = true;
              trigger.style.cursor = 'grabbing';
              wpPlaceAt(wpDrag.left0 + dx, wpDrag.top0 + dy);
            }, true);

            window.addEventListener('mouseup', function () {
              if (!wpDrag) return;
              var moved = wpDrag.moved;
              wpDrag = null;
              trigger.style.cursor = 'pointer';
              if (!moved) return;
              wpSuppressClick = true;
              wpDragAt = Date.now();
              setTimeout(function () { wpSuppressClick = false; }, 0);
              var r = box.getBoundingClientRect();
              wpLog('up moved=' + moved + ' at ' + Math.round(r.left) + ',' + Math.round(r.top));
              wpSavePos(r.left, r.top);
              wpSyncPanelSide();   // 拖到顶部后，面板要改到下方展开
            }, true);

            // 归位不再用双击：「点开面板 → 点关面板」本身就是两次点击，
            // 会落进浏览器的双击判定，于是每次开合面板都把挂件归位一次。
            // 改成面板里的「挂件归位」按钮（见下方 wp-home），语义明确，不会再被误触。

            trigger.addEventListener('click', function () {
              if (wpSuppressClick) return;   // 刚拖完：这次 click 是拖动的尾巴
              box.classList.toggle('open');
              if (box.classList.contains('open')) {
                wpSyncPanelSide();
                refresh();
              }
            });

            // 位置优先级：本页拖过的 > 配置里存的 > 默认右下角
            // （前一档是为了让「挂件 DOM 被页面重建」时也不往回弹）
            var wpSavedPos = window.__alifeWpPos;
            if (!wpSavedPos) {
              try {
                var rawPos = sessionStorage.getItem('alifeWpPos');
                if (rawPos) wpSavedPos = JSON.parse(rawPos);
              } catch (e) { }
            }
            if (wpSavedPos && typeof wpSavedPos.x === 'number') {
              window.__alifeWpPos = { x: wpSavedPos.x, y: wpSavedPos.y };
              wpPlaceAt(wpSavedPos.x, wpSavedPos.y);
              wpLog('create from page ' + wpSavedPos.x + ',' + wpSavedPos.y);
            } else if (typeof widgetX === 'number' && widgetX >= 0 && typeof widgetY === 'number' && widgetY >= 0) {
              wpPlaceAt(widgetX, widgetY);
              wpLog('create from cfg ' + widgetX + ',' + widgetY);
            } else {
              wpLog('create default');
            }

            box.querySelector('.wp-close').addEventListener('click', function () {
              box.classList.remove('open');
            });

            if (scrimBtn) {
              scrimBtn.addEventListener('click', function () {
                var next = scrimBtn.getAttribute('data-white') === '1' ? '0' : '1';
                call('api/config?white=' + next).then(render);
              });
            }

            if (audioBtn) {
              audioBtn.addEventListener('click', function () {
                if (audioBtn.disabled) return;

                var nextMute = audioBtn.getAttribute('data-on') === '1' ? '1' : '0';
                noAudio = false;

                call('api/config?mute=' + nextMute).then(function (state) {
                  render(state);
                  if (nextMute !== '0') return;

                  // 取消静音 1.2s 后看有没有解码到音频字节：还是 0 就说明这段壁纸本身没有音轨
                  setTimeout(function () {
                    var v = document.querySelector('#alife-wallpaper video');
                    if (!v || v.paused || v.muted) return;
                    if (typeof v.webkitAudioDecodedByteCount !== 'number') return;
                    if (v.webkitAudioDecodedByteCount > 0) return;

                    noAudio = true;
                    refresh();
                  }, 1200);
                });
              });
            }

            var clearBtn = box.querySelector('.wp-clear');
            if (clearBtn) {
              clearBtn.addEventListener('click', function () {
                if (!window.confirm('清除当前壁纸？挂件会保留，随时可以再选一张。')) return;
                call('api/clear').then(render);
              });
            }

            var homeBtn = box.querySelector('.wp-home');
            if (homeBtn) {
              homeBtn.addEventListener('click', function () {
                wpPlaceDefault();
                wpSavePos(-1, -1);
                box.classList.remove('open');
                wpLog('home');
              });
            }

            return 'ok';
          };

          return 'init';
        })();
        """;

    readonly ILogger logger;

    /// <summary>
    /// 页面脚本执行器。Alife 5.0 起没有 Electron 窗口，只能由 GlobalUI 组件
    /// 通过 IJSRuntime 提供；为空说明主界面还没挂载，注入要等下一次。
    /// </summary>
    internal static Func<string, Task<string>>? JsRunner;

    string? currentKey;

    public AlifeBackground(ILogger logger) => this.logger = logger;

    /// <summary>找到 Alife 主窗口并确保注入脚本已加载（页面未就绪时重试）。</summary>
    public async Task AttachAsync()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (JsRunner == null)
                {
                    await Task.Delay(300);
                    continue;
                }

                await ExecuteAsync(InitScript);
                return;
            }
            catch
            {
                await Task.Delay(300);
            }
        }

        throw new InvalidOperationException("Alife 主窗口 / 页面尚未就绪，无法注入背景");
    }

    public async Task ShowAsync(WallpaperItem item, WallpaperConfig config, string baseUrl)
    {
        await AttachAsync();

        string key = string.Join("|",
            item.Id,
            config.Opacity,
            config.Blur,
            config.ContainFit ? "contain" : "cover",
            config.SceneScripts ? "scripts" : "noscripts");

        if (key == currentKey && await IsLayerAliveAsync()) return;

        // 网页壁纸走 /wph/：那份 HTML 会被注入属性种子与 postMessage 接收端
        string entryPrefix = string.Equals(item.Type, "web", StringComparison.OrdinalIgnoreCase) ? "wph/" : "wp/";

        string url = item.IsPlayable
            ? baseUrl + entryPrefix + EscapePath(item.File) + "?v=" + item.Id
            : "";

        string preview = string.IsNullOrEmpty(item.Preview)
            ? ""
            : baseUrl + "wp/" + EscapePath(item.Preview) + "?v=" + item.Id;

        // 场景壁纸：把 scene.pkg 暴露给页面，交给 WebWallGL 实时渲染；找不到包就继续用预览图
        string scenePkg = ResolveScenePkg(item) ?? "";
        string pkg = scenePkg.Length == 0 ? "" : baseUrl + "wp/" + EscapePath(scenePkg) + "?v=" + item.Id;

        string payload = JsonSerializer.Serialize(new
        {
            key,
            kind = item.IsPlayable ? item.Type : "preview",
            url,
            preview,
            pkg,
            liveScene = config.LiveScene && pkg.Length > 0,
            // 属性面板里的数值要靠场景脚本才生效，这个标记同时给挂件用来提示
            scripts = config.SceneScripts,
            lib = baseUrl + "lib/webwallgl.global.min.js",
            fit = config.ContainFit ? "contain" : "cover",
            soften = !item.IsPlayable,
            mute = config.Mute,
            volume = Math.Clamp(config.Volume, 0, 200),
            opacity = Math.Clamp(config.Opacity, 0, 100) / 100.0,
            blur = Math.Clamp(config.Blur, 0, 30),
            panel = Math.Clamp(config.PanelOpacity, 0, 100) / 100.0,
            bubble = Math.Clamp(config.BubbleOpacity, 0, 100) / 100.0,
            // 右下角挂件设置面板自己的不透明度：0 = 全透明，能看穿到壁纸；跟页面卡片的 panel / 白度 无关
            wpanel = Math.Clamp(config.WidgetPanelOpacity, 0, 100) / 100.0,
            whiteScrim = config.WhiteScrim,
            brightness = Math.Clamp(config.Brightness, 0, 200) / 100.0,
            contrast = Math.Clamp(config.Contrast, 0, 200) / 100.0,
            saturate = Math.Clamp(config.Saturate, 0, 200) / 100.0,
            rate = Math.Clamp(config.PlaybackRate, 50, 200) / 100.0,
            fade = config.FadeTransition,
            widget = config.ShowWidget,
            // 右下角挂件的形象：0 = 经典胶囊，1 / 2 = Q 版大肥鱼
            petAvatar = Math.Clamp(config.PetAvatar, 0, 2),
            petSize = Math.Clamp(config.PetSize, 56, 120),
            // 上次拖动的位置；-1 表示没拖过，用默认右下角
            widgetX = config.WidgetX,
            widgetY = config.WidgetY,
            // properties 给挂载用（库内部会包一层 {value}），propsNative 给原生通道用（要 {value,type}）
            properties = WallpaperProps.Overrides(WallpaperProps.Resolve(item, WallpaperProps.Store.Get(item.Id))),
            propsNative = WallpaperProps.NativeNodes(WallpaperProps.Resolve(item, WallpaperProps.Store.Get(item.Id))),
            parallax = Math.Clamp(config.Parallax, 0, 100),
            api = baseUrl
        });

        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                string result = await ExecuteAsync("window.__alifeWallpaperShow(" + payload + ");");
                if (result != "wait") { currentKey = key; return; }
            }
            catch
            {
                // 页面还没准备好：下面重新 Attach 后再试一轮
            }

            await Task.Delay(300);
            await AttachAsync();
        }

        throw new InvalidOperationException("Alife 页面尚未准备好，壁纸注入失败");
    }

    /// <summary>只刷新样式（遮罩 / 面板 / 模糊），不重建媒体元素，挂件拖滑块时用。</summary>
    public async Task RestyleAsync(WallpaperConfig config)
    {
        if (JsRunner == null) return;

        string payload = JsonSerializer.Serialize(new
        {
            opacity = Math.Clamp(config.Opacity, 0, 100) / 100.0,
            blur = Math.Clamp(config.Blur, 0, 30),
            panel = Math.Clamp(config.PanelOpacity, 0, 100) / 100.0,
            bubble = Math.Clamp(config.BubbleOpacity, 0, 100) / 100.0,
            parallax = Math.Clamp(config.Parallax, 0, 100),
            petAvatar = Math.Clamp(config.PetAvatar, 0, 2),
            petSize = Math.Clamp(config.PetSize, 56, 120),
            wpanel = Math.Clamp(config.WidgetPanelOpacity, 0, 100) / 100.0,
            whiteScrim = config.WhiteScrim,
            brightness = Math.Clamp(config.Brightness, 0, 200) / 100.0,
            contrast = Math.Clamp(config.Contrast, 0, 200) / 100.0,
            saturate = Math.Clamp(config.Saturate, 0, 200) / 100.0
        });

        try
        {
            await ExecuteAsync("window.__alifeWallpaperRestyle && window.__alifeWallpaperRestyle(" + payload + ");");
        }
        catch (Exception ex)
        {
            logger.LogWarning("[WallpaperEngine] 应用界面参数失败：" + ex.Message);
        }
    }

    /// <summary>重新挂载场景（实时渲染退回预览图时用它救回来，不用切壁纸）。</summary>
    public async Task<string> RemountSceneAsync()
    {
        if (JsRunner == null) return "no-window";

        const string js = """
            (function () {
              var cfg = window.__alifeWpCfg;
              if (!cfg || !cfg.pkg) return 'no-pkg';
              if (document.hidden) return 'hidden';
              if (document.getElementById('alife-wp-scene') && window.__alifeWpScene) return 'alive';

              cfg.key = String(cfg.key).replace(/\|remount\d+$/, '') + '|remount' + Date.now();
              window.__alifeWallpaperShow(cfg);
              return 'remounting';
            })()
            """;

        try
        {
            string result = (await ExecuteAsync(js)).Trim();
            if (result == "hidden") return "{\"ok\":false,\"error\":\"窗口在后台时不能重挂，切到前台再点\"}";

            // 首帧要十几秒（pkg 200MB+），先把"正在挂"报回去，面板自己刷新状态
            return result == "remounting" ? "{\"ok\":true}" : "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(result) + "}";
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(ex.Message) + "}";
        }
    }

    /// <summary>在页面里跑一段 JS（只由 127.0.0.1 的 /api/eval 调用，用于本机排查）。</summary>
    public async Task<string> EvalAsync(string script)
    {
        if (JsRunner == null) return "no-window";

        try { return await ExecuteAsync(script); }
        catch (Exception ex) { return "ERR: " + ex.Message; }
    }

    /// <summary>
    /// 场景运行时诊断：脚本是否被跳过 / 报了什么错 / 属性值现在是多少。
    /// 属性调了画面不动时，先看这里——大多数情况是场景脚本挂了，或者根本没挂上引擎。
    /// </summary>
    public async Task<string> SceneDiagAsync()
    {
        if (JsRunner == null) return "{\"ok\":false,\"error\":\"主窗口未就绪\"}";

        const string probe = """
            (function () {
              var cfg = window.__alifeWpCfg || {};
              var i = window.__alifeWpScene || null;
              var out = {
                ver: window.__alifeWpVer || '',
                key: cfg.key || '',
                kind: cfg.kind || '',
                scripts: cfg.scripts,
                liveScene: cfg.liveScene,
                hasScene: !!i,
                sceneHost: !!document.getElementById('alife-wp-scene'),
                canvas: null,
                api: '',
                props: {},
                issues: [],
                skipped: null,
                frameTicks: null
              };

              try { out.props = (i && i.getProperties) ? i.getProperties() : {}; } catch (e) { out.propsErr = String(e); }
              try { out.api = i ? Object.keys(i).filter(function (k) { return typeof i[k] === 'function'; }).join(',') : ''; } catch (e) { }

              // 脚本错误 / 被跳过的脚本都在内部对象的 editor 上，facade 上取不到
              try {
                var link = globalThis[Symbol.for('webwallgl.runtime-link')];
                var ctx = (link && link.get) ? link.get(i) : null;
                var ed = ctx && ctx.sceneCtl && ctx.sceneCtl.editor;

                out.hasEditor = !!ed;
                out.hasNative = !!(ed && typeof ed.declareUserProperties === 'function');
                out.issues = (ed && ed.getScriptIssues) ? ed.getScriptIssues().slice(0, 12) : [];
                out.skipped = (ed && ed.getSkippedScripts) ? ed.getSkippedScripts() : null;
              } catch (e) { out.editorErr = String(e); }

              var c = document.querySelector('#alife-wp-scene canvas');
              out.canvas = c ? [c.width, c.height] : null;

              return JSON.stringify(out);
            })()
            """;

        try { return await ExecuteAsync(probe); }
        catch (Exception ex) { return "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(ex.Message) + "}"; }
    }

    /// <summary>把页面上“浅色不透明”的元素列出来，供 /api/dom 排查（只在本机回环上暴露）。</summary>
    public async Task<string> DumpDomAsync()
    {
        if (JsRunner == null) return "";

        try
        {
            return await ExecuteAsync("window.__alifeWallpaperDump ? window.__alifeWallpaperDump() : ''");
        }
        catch
        {
            return "";
        }
    }

    /// <param name="keepWidget">true = 只清壁纸、保留右下角挂件（用户主动“清除壁纸”时用）。</param>
    public async Task ClearAsync(bool keepWidget = false)
    {
        currentKey = null;
        if (JsRunner == null) return;

        string arg = keepWidget ? "true" : "false";

        try { await ExecuteAsync("window.__alifeWallpaperClear && window.__alifeWallpaperClear(" + arg + ");"); }
        catch (Exception ex) { logger.LogWarning("[WallpaperEngine] 清除界面背景失败：" + ex.Message); }
    }

    /// <summary>改用户属性：场景走 setProperties（不重挂 pkg），网页走 postMessage 桥。</summary>
    /// <returns>页面侧的实际落点（scene / web / no-scene / scripts-off / no-window），供挂件提示用。</returns>
    public async Task<string> ApplyPropsAsync(Dictionary<string, object?> props)
    {
        if (props.Count == 0) return "";
        if (JsRunner == null) return "no-window";

        string json = JsonSerializer.Serialize(props);

        try
        {
            string result = await ExecuteAsync("window.__alifeWallpaperSetProps ? window.__alifeWallpaperSetProps(" + json + ") : 'no-page'");
            return result.Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning("[WallpaperEngine] 应用壁纸属性失败：" + ex.Message);
            return "error";
        }
    }

    /// <summary>音量 / 静音 / 倍速这类只影响媒体元素的参数，不重建壁纸层，直接改。</summary>
    public async Task ApplyMediaAsync(WallpaperConfig config)
    {
        if (JsRunner == null) return;

        string payload = JsonSerializer.Serialize(new
        {
            mute = config.Mute,
            volume = Math.Clamp(config.Volume, 0, 200),
            rate = Math.Clamp(config.PlaybackRate, 50, 200) / 100.0
        });

        try { await ExecuteAsync("window.__alifeWallpaperMedia && window.__alifeWallpaperMedia(" + payload + ");"); }
        catch (Exception ex) { logger.LogWarning("[WallpaperEngine] 应用声音参数失败：" + ex.Message); }
    }

    public async Task<bool> IsLayerAliveAsync()
    {
        if (JsRunner == null) return false;

        try
        {
            string result = await ExecuteAsync(
                "(function(){ if (window.__alifeWallpaperTransparent) window.__alifeWallpaperTransparent();" +
                " return !!document.getElementById('alife-wallpaper'); })()");
            return string.Equals(result?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    async Task<string> ExecuteAsync(string script)
    {
        var runner = JsRunner;

        if (runner == null) throw new InvalidOperationException("主窗口未就绪");

        return await runner(script) ?? "";
    }

    /// <summary>找场景壁纸的 scene.pkg（Wallpaper Engine 项目里固定叫这个名）。</summary>
    static string? ResolveScenePkg(WallpaperItem item)
    {
        if (!string.Equals(item.Type, "scene", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (File.Exists(Path.Combine(item.Root, "scene.pkg"))) return "scene.pkg";

            foreach (string candidate in Directory.EnumerateFiles(item.Root, "*.pkg", SearchOption.TopDirectoryOnly))
                return Path.GetFileName(candidate);
        }
        catch { }

        return null;
    }

    static string EscapePath(string relative)
        => string.Join("/", relative.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));
}
