/* HD2ModManager 用户图文教程 — 离线交互脚本（兼容 file://，无外部依赖） */
(function () {
  'use strict';

  function init() {
    var lightbox = createLightbox();
    initShots(lightbox);
    initStepPager();
  }

  /* ------------------------------------------------------------------
     1. 截图组件：缺图占位、编号标注联动、点击放大
  ------------------------------------------------------------------ */
  function initShots(lightbox) {
    var figures = document.querySelectorAll('figure.shot');
    for (var i = 0; i < figures.length; i++) {
      setupShotFigure(figures[i], lightbox);
    }
  }

  function setupShotFigure(figure, lightbox) {
    var src = figure.getAttribute('data-src') || '';
    var alt = figure.getAttribute('data-alt') || ('操作界面截图：' + src);
    var hint = figure.getAttribute('data-hint') || '将对应界面截图保存为此文件名后，刷新本页即可显示。';

    // 收集页面里声明的 <a class="pin" style="left:...;top:..." data-step="1">说明</a>
    var rawPins = Array.prototype.slice.call(figure.querySelectorAll('a.pin'));
    var pinsData = [];
    for (var i = 0; i < rawPins.length; i++) {
      var raw = rawPins[i];
      var stepNum = raw.getAttribute('data-step') || String(i + 1);
      var caption = (raw.textContent || '').replace(/\s+/g, ' ').trim();
      pinsData.push({
        step: stepNum,
        left: raw.style.left || '50%',
        top: raw.style.top || '50%',
        caption: caption
      });
    }

    // 清空原始声明，重建统一交互结构
    figure.innerHTML = '';

    var stage = document.createElement('div');
    stage.className = 'shot-stage';

    var img = document.createElement('img');
    img.className = 'shot-img';
    img.alt = alt;
    if (src) {
      img.src = src;
    } else {
      stage.classList.add('is-missing');
    }

    var placeholder = document.createElement('div');
    placeholder.className = 'shot-placeholder';
    var fileBadge = document.createElement('div');
    fileBadge.className = 'shot-placeholder-file';
    fileBadge.textContent = src || '未指定截图文件名';
    var descEl = document.createElement('div');
    descEl.className = 'shot-placeholder-desc';
    descEl.textContent = hint;
    placeholder.appendChild(fileBadge);
    placeholder.appendChild(descEl);

    function markMissing() {
      stage.classList.add('is-missing');
    }

    img.addEventListener('error', markMissing);
    img.addEventListener('load', function () {
      if (img.naturalWidth === 0) {
        markMissing();
      } else {
        stage.classList.remove('is-missing');
      }
    });

    stage.appendChild(img);
    stage.appendChild(placeholder);

    var zoomBtn = document.createElement('button');
    zoomBtn.type = 'button';
    zoomBtn.className = 'shot-zoom-btn';
    zoomBtn.textContent = '放大查看';
    stage.appendChild(zoomBtn);

    // 编号标注按钮、高亮说明条、图例列表
    var pinButtons = [];
    var legendItems = [];
    var calloutEl = null;
    var calloutBadge = null;
    var calloutText = null;
    var activeIndex = -1;

    if (pinsData.length > 0) {
      calloutEl = document.createElement('div');
      calloutEl.className = 'shot-callout';
      calloutBadge = document.createElement('span');
      calloutBadge.className = 'shot-callout-badge';
      calloutText = document.createElement('span');
      calloutEl.appendChild(calloutBadge);
      calloutEl.appendChild(calloutText);
    }

    var legendList = document.createElement('ol');
    legendList.className = 'shot-legend';

    function selectPin(idx) {
      if (idx < 0 || idx >= pinsData.length) return;
      activeIndex = idx;
      for (var k = 0; k < pinButtons.length; k++) {
        var on = k === idx;
        pinButtons[k].classList.toggle('is-active', on);
        pinButtons[k].setAttribute('aria-pressed', on ? 'true' : 'false');
        legendItems[k].classList.toggle('is-active', on);
      }
      if (calloutEl && calloutBadge && calloutText) {
        calloutBadge.textContent = pinsData[idx].step;
        calloutText.textContent = pinsData[idx].caption;
      }
    }

    for (var j = 0; j < pinsData.length; j++) {
      (function (index, data) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'pin';
        btn.style.left = data.left;
        btn.style.top = data.top;
        btn.setAttribute('data-step', data.step);
        btn.setAttribute('aria-label', '标注 ' + data.step + '：' + data.caption);
        btn.title = data.step + '. ' + data.caption;
        btn.textContent = data.step;
        btn.addEventListener('click', function (e) {
          e.preventDefault();
          e.stopPropagation();
          selectPin(index);
        });
        stage.appendChild(btn);
        pinButtons.push(btn);

        var li = document.createElement('li');
        var itemBtn = document.createElement('button');
        itemBtn.type = 'button';
        itemBtn.className = 'shot-legend-item';
        var numSpan = document.createElement('span');
        numSpan.className = 'shot-legend-num';
        numSpan.textContent = data.step;
        var textSpan = document.createElement('span');
        textSpan.textContent = data.caption;
        itemBtn.appendChild(numSpan);
        itemBtn.appendChild(textSpan);
        itemBtn.addEventListener('click', function () {
          selectPin(index);
        });
        li.appendChild(itemBtn);
        legendList.appendChild(li);
        legendItems.push(itemBtn);
      })(j, pinsData[j]);
    }

    figure.appendChild(stage);
    if (calloutEl) {
      figure.appendChild(calloutEl);
      selectPin(0);
    }
    if (pinsData.length > 0) {
      figure.appendChild(legendList);
    }

    function openCurrentLightbox(triggerEl) {
      var missing = stage.classList.contains('is-missing');
      lightbox.open({
        src: src,
        alt: alt,
        hint: hint,
        missing: missing,
        pins: pinsData,
        activeIndex: activeIndex >= 0 ? activeIndex : 0,
        trigger: triggerEl
      });
    }

    img.addEventListener('click', function () {
      openCurrentLightbox(img);
    });
    placeholder.addEventListener('click', function () {
      openCurrentLightbox(placeholder);
    });
    zoomBtn.addEventListener('click', function (e) {
      e.stopPropagation();
      openCurrentLightbox(zoomBtn);
    });

    // 若图片在脚本执行前已完成同步判断且失败（file:// 下常见）
    if (img.complete && src && img.naturalWidth === 0) {
      markMissing();
    }
  }

  /* ------------------------------------------------------------------
     2. 全屏放大遮罩（Esc / 点击背景 / 关闭按钮 关闭）
  ------------------------------------------------------------------ */
  function createLightbox() {
    var root = document.createElement('div');
    root.className = 'lightbox';
    root.setAttribute('role', 'dialog');
    root.setAttribute('aria-modal', 'true');
    root.setAttribute('aria-label', '截图放大查看');
    root.hidden = true;

    root.innerHTML =
      '<div class="lightbox-dialog">' +
      '  <div class="lightbox-bar">' +
      '    <span class="lightbox-title"></span>' +
      '    <button type="button" class="lightbox-close">关闭 (Esc)</button>' +
      '  </div>' +
      '  <div class="lightbox-body">' +
      '    <div class="lightbox-stage"></div>' +
      '  </div>' +
      '  <div class="lightbox-caption"></div>' +
      '</div>';

    document.body.appendChild(root);

    var titleEl = root.querySelector('.lightbox-title');
    var closeBtn = root.querySelector('.lightbox-close');
    var stageEl = root.querySelector('.lightbox-stage');
    var captionEl = root.querySelector('.lightbox-caption');
    var lastTrigger = null;

    function close() {
      if (root.hidden) return;
      root.hidden = true;
      document.body.style.overflow = '';
      if (lastTrigger && typeof lastTrigger.focus === 'function') {
        lastTrigger.focus();
      }
      lastTrigger = null;
    }

    closeBtn.addEventListener('click', close);
    root.addEventListener('click', function (e) {
      if (e.target === root) {
        close();
      }
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && !root.hidden) {
        e.preventDefault();
        close();
      }
    });

    function open(opts) {
      lastTrigger = opts.trigger || null;
      var showSrc = opts.src && opts.src.indexOf('data:') !== 0;
      titleEl.textContent = showSrc ? (opts.alt + '（' + opts.src + '）') : opts.alt;
      stageEl.innerHTML = '';

      if (opts.missing || !opts.src) {
        var ph = document.createElement('div');
        ph.className = 'lightbox-placeholder';
        var f = document.createElement('div');
        f.className = 'shot-placeholder-file';
        f.textContent = opts.src || '未指定截图文件名';
        var h = document.createElement('div');
        h.textContent = opts.hint || '截图待补充';
        ph.appendChild(f);
        ph.appendChild(h);
        stageEl.appendChild(ph);
      } else {
        var img = document.createElement('img');
        img.src = opts.src;
        img.alt = opts.alt;
        stageEl.appendChild(img);
      }

      var pinEls = [];
      function showPinCaption(idx) {
        if (!opts.pins || !opts.pins.length) {
          captionEl.hidden = true;
          return;
        }
        captionEl.hidden = false;
        var safeIdx = (idx >= 0 && idx < opts.pins.length) ? idx : 0;
        for (var i = 0; i < pinEls.length; i++) {
          pinEls[i].classList.toggle('is-active', i === safeIdx);
        }
        var p = opts.pins[safeIdx];
        captionEl.textContent = '标注 ' + p.step + '：' + p.caption;
      }

      if (opts.pins && opts.pins.length) {
        for (var i = 0; i < opts.pins.length; i++) {
          (function (idx, data) {
            var pin = document.createElement('button');
            pin.type = 'button';
            pin.className = 'pin';
            pin.style.left = data.left;
            pin.style.top = data.top;
            pin.textContent = data.step;
            pin.title = data.step + '. ' + data.caption;
            pin.addEventListener('click', function (e) {
              e.stopPropagation();
              showPinCaption(idx);
            });
            stageEl.appendChild(pin);
            pinEls.push(pin);
          })(i, opts.pins[i]);
        }
        showPinCaption(opts.activeIndex || 0);
      } else {
        captionEl.hidden = true;
      }

      root.hidden = false;
      document.body.style.overflow = 'hidden';
      closeBtn.focus();
    }

    return { open: open, close: close };
  }

  /* ------------------------------------------------------------------
     3. 步骤「上一步 / 下一步」+ 保留整页滚动阅读
  ------------------------------------------------------------------ */
  function initStepPager() {
    var article = document.querySelector('article');
    if (!article) return;

    var steps = Array.prototype.slice.call(article.querySelectorAll('.step[data-step]'));
    if (steps.length === 0) return;

    var pagerHost = article.querySelector('.step-pager');
    var currentIndex = 0;

    var labelEl = null;
    var pills = [];
    var prevBtn = null;
    var nextBtn = null;

    if (pagerHost) {
      pagerHost.innerHTML = '';

      var statusWrap = document.createElement('div');
      statusWrap.className = 'step-pager-status';

      labelEl = document.createElement('span');
      labelEl.className = 'step-pager-label';
      statusWrap.appendChild(labelEl);

      var pillsWrap = document.createElement('div');
      pillsWrap.className = 'step-pills';
      for (var i = 0; i < steps.length; i++) {
        (function (idx) {
          var btn = document.createElement('button');
          btn.type = 'button';
          btn.className = 'step-pill';
          btn.textContent = steps[idx].getAttribute('data-step') || String(idx + 1);
          btn.addEventListener('click', function () {
            goToStep(idx, true);
          });
          pillsWrap.appendChild(btn);
          pills.push(btn);
        })(i);
      }
      statusWrap.appendChild(pillsWrap);

      var actionsWrap = document.createElement('div');
      actionsWrap.className = 'step-pager-actions';

      prevBtn = document.createElement('button');
      prevBtn.type = 'button';
      prevBtn.className = 'btn';
      prevBtn.textContent = '上一步';
      prevBtn.addEventListener('click', function () {
        goToStep(currentIndex - 1, true);
      });

      nextBtn = document.createElement('button');
      nextBtn.type = 'button';
      nextBtn.className = 'btn btn-primary';
      nextBtn.textContent = '下一步';
      nextBtn.addEventListener('click', function () {
        goToStep(currentIndex + 1, true);
      });

      actionsWrap.appendChild(prevBtn);
      actionsWrap.appendChild(nextBtn);

      pagerHost.appendChild(statusWrap);
      pagerHost.appendChild(actionsWrap);
    }

    // 为每张步骤卡片底部补充「上一步 / 下一步」按钮
    var inlineFooters = [];
    for (var s = 0; s < steps.length; s++) {
      (function (idx) {
        var stepEl = steps[idx];
        var footer = document.createElement('div');
        footer.className = 'step-footer';

        var hint = document.createElement('span');
        hint.className = 'step-footer-hint';
        hint.textContent = '第 ' + (idx + 1) + ' 步 / 共 ' + steps.length + ' 步';

        var btns = document.createElement('div');
        btns.className = 'step-footer-btns';

        var pBtn = document.createElement('button');
        pBtn.type = 'button';
        pBtn.className = 'btn';
        pBtn.textContent = '上一步';
        pBtn.disabled = idx === 0;
        pBtn.addEventListener('click', function () {
          goToStep(idx - 1, true);
        });

        var nBtn = document.createElement('button');
        nBtn.type = 'button';
        nBtn.className = 'btn btn-primary';
        nBtn.textContent = idx === steps.length - 1 ? '已是最后一步' : '下一步';
        nBtn.disabled = idx === steps.length - 1;
        nBtn.addEventListener('click', function () {
          goToStep(idx + 1, true);
        });

        btns.appendChild(pBtn);
        btns.appendChild(nBtn);
        footer.appendChild(hint);
        footer.appendChild(btns);
        stepEl.appendChild(footer);
        inlineFooters.push(footer);
      })(s);
    }

    function updateUi(idx) {
      currentIndex = idx;
      for (var i = 0; i < steps.length; i++) {
        steps[i].classList.toggle('is-active', i === idx);
        if (pills[i]) {
          pills[i].classList.toggle('is-active', i === idx);
        }
      }
      if (labelEl) {
        labelEl.textContent = '步骤 ' + (idx + 1) + ' / ' + steps.length;
      }
      if (prevBtn) {
        prevBtn.disabled = idx <= 0;
      }
      if (nextBtn) {
        nextBtn.disabled = idx >= steps.length - 1;
        nextBtn.textContent = idx >= steps.length - 1 ? '最后一步' : '下一步';
      }
    }

    function goToStep(idx, scroll) {
      if (idx < 0 || idx >= steps.length) return;
      updateUi(idx);
      if (scroll) {
        steps[idx].scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    }

    // 滚动阅读时自动同步当前所在步骤高亮
    var scrollTick = false;
    window.addEventListener('scroll', function () {
      if (scrollTick) return;
      scrollTick = true;
      window.requestAnimationFrame(function () {
        scrollTick = false;
        var anchorY = 160;
        var bestIdx = 0;
        var bestDist = Infinity;
        for (var i = 0; i < steps.length; i++) {
          var rect = steps[i].getBoundingClientRect();
          if (rect.top <= anchorY && rect.bottom >= anchorY) {
            bestIdx = i;
            break;
          }
          var dist = Math.abs(rect.top - anchorY);
          if (dist < bestDist) {
            bestDist = dist;
            bestIdx = i;
          }
        }
        if (bestIdx !== currentIndex) {
          updateUi(bestIdx);
        }
      });
    }, { passive: true });

    updateUi(0);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
