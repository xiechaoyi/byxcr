namespace Byxcr.Services;

/// <summary>
/// 根路由 <c>/</c> 返回的内置 Web 控制台页面。整个页面（HTML + CSS + JS）以常量形式内嵌在程序里：
/// 单文件 AOT 发布下没有任何额外静态文件依赖，也不用读磁盘，工作目录不可写时同样能正常返回。
/// <para>页面自己调用<b>公开接口</b> <c>/api/list</c>（支持 filter / enabled / page / limit 四个查询参数），
/// 所以浏览器里不需要填任何令牌就能浏览本服务真正在同步的任务；增删改仍走需要令牌的写接口。</para>
/// <para>表格内容全部通过 <c>textContent</c> 写入 DOM（不使用 innerHTML），镜像名/路径里的特殊字符不会被当成 HTML。</para>
/// </summary>
internal static class WebConsole
{
    /// <summary>控制台页面正文：无外部资源依赖（图标走 data: URI），可离线打开。</summary>
    public const string Html = """"
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>byxcr · 镜像搬运箱</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='8' fill='%232563eb'/%3E%3Cpath d='M9 21V11l4 6 3-4 3 4 4-6v10' fill='none' stroke='%23fff' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E">
<style>
:root{
  --bg:#f5f6f8; --panel:#ffffff; --border:#e3e6eb; --text:#1f2430; --muted:#6b7280;
  --accent:#2563eb; --accent-soft:#eaf1ff; --chip:#f2f4f8;
  --ok:#0f7b3f; --ok-soft:#e7f6ec; --warn:#a15c07; --warn-soft:#fdf3e2;
  --err:#b42318; --err-soft:#fdecea; --neu:#475467; --neu-soft:#f1f3f7;
  --shadow:0 1px 2px rgba(16,24,40,.05),0 1px 3px rgba(16,24,40,.08);
}
@media (prefers-color-scheme: dark){
  :root{
    --bg:#0f1115; --panel:#161a21; --border:#262d38; --text:#e6e9ef; --muted:#98a2b3;
    --accent:#6ba1ff; --accent-soft:#1b2534; --chip:#1d232d;
    --ok:#7ee2a8; --ok-soft:#16311f; --warn:#f7c774; --warn-soft:#382b13;
    --err:#f9a8a0; --err-soft:#38201d; --neu:#c1c7d0; --neu-soft:#222936;
    --shadow:none;
  }
}
*{box-sizing:border-box}
html,body{margin:0}
body{
  font:14px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI","PingFang SC","Hiragino Sans GB","Microsoft YaHei",sans-serif;
  background:var(--bg); color:var(--text); padding:18px 20px 26px;
}
.top{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:14px}
.brand{display:flex;align-items:center;gap:9px}
.dot{width:10px;height:10px;border-radius:50%;background:var(--accent);box-shadow:0 0 0 4px var(--accent-soft)}
.brand h1{margin:0;font-size:17px;font-weight:650;letter-spacing:.2px}
.brand .tag{font-size:12px;color:var(--muted);background:var(--panel);border:1px solid var(--border);border-radius:999px;padding:2px 9px}
.actions{display:flex;align-items:center;gap:10px}
.muted{color:var(--muted);font-size:12.5px}
.btn{
  font:inherit;font-size:13px;padding:6px 13px;border-radius:8px;border:1px solid var(--border);
  background:var(--panel);color:var(--text);cursor:pointer;transition:.15s;
}
.btn:hover{border-color:var(--accent);color:var(--accent)}
.btn:active{transform:translateY(1px)}
.bar{
  display:flex;align-items:center;gap:10px;flex-wrap:wrap;
  background:var(--panel);border:1px solid var(--border);border-radius:10px;
  padding:10px 12px;box-shadow:var(--shadow);margin-bottom:12px;
}
.field{display:flex;align-items:center}
.field.grow{flex:1 1 240px}
.field select{width:auto;min-width:120px}
input[type=search],select{
  font:inherit;font-size:13px;padding:7px 10px;width:100%;
  border:1px solid var(--border);border-radius:8px;background:var(--bg);color:var(--text);outline:none;
}
input[type=search]:focus,select:focus{border-color:var(--accent);box-shadow:0 0 0 3px var(--accent-soft)}
.check{display:flex;align-items:center;gap:6px;color:var(--muted);font-size:12.5px;white-space:nowrap;user-select:none}
.check input{accent-color:var(--accent)}
.link{color:var(--muted);font-size:12.5px;text-decoration:none;border-bottom:1px dashed var(--border)}
.link:hover{color:var(--accent);border-color:var(--accent)}
.alert{
  margin-bottom:12px;padding:10px 12px;border-radius:10px;font-size:13px;
  background:var(--err-soft);color:var(--err);border:1px solid var(--err);
}
.panel{background:var(--panel);border:1px solid var(--border);border-radius:10px;box-shadow:var(--shadow);overflow:hidden}
.table-wrap{overflow-x:auto}
table{border-collapse:collapse;width:100%;min-width:960px}
th,td{padding:9px 12px;text-align:left;border-bottom:1px solid var(--border);vertical-align:middle}
th{
  background:var(--chip);color:var(--muted);font-size:12px;font-weight:600;letter-spacing:.3px;
  white-space:nowrap;position:sticky;top:0;
}
tbody tr:hover{background:var(--accent-soft)}
tbody tr:last-child td{border-bottom:none}
td .mono{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,"Courier New",monospace;font-size:12.5px;word-break:break-all}
td .sub{margin-top:2px;font-size:11.5px;color:var(--muted);word-break:break-all}
.dur{font-variant-numeric:tabular-nums;white-space:nowrap}
.hash{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,"Courier New",monospace;font-size:12px;color:var(--muted)}
td.file{max-width:330px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.chip{
  display:inline-flex;align-items:center;margin:0 4px 0 0;padding:3px 8px;
  font-size:11.5px;line-height:1.2;border-radius:999px;white-space:nowrap;
  background:var(--neu-soft);color:var(--neu);
}
.chip.on,.chip.success,.chip.skipped{background:var(--ok-soft);color:var(--ok)}
.chip.running{background:var(--accent-soft);color:var(--accent)}
.chip.failed{background:var(--err-soft);color:var(--err)}
/* 带有悬停说明的状态徽标：鼠标移上去给 help 光标，提示还有更多信息 */
.chip[title]{cursor:help}
.empty{padding:46px 0;text-align:center;color:var(--muted)}
.foot{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-top:12px}
.pager{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.pg{
  min-width:32px;height:30px;padding:0 8px;font:inherit;font-size:12.5px;cursor:pointer;
  border:1px solid var(--border);border-radius:8px;background:var(--panel);color:var(--text);transition:.15s;
}
.pg:hover:not(:disabled):not(.current){border-color:var(--accent);color:var(--accent)}
.pg.current{background:var(--accent);border-color:var(--accent);color:#fff;font-weight:600;cursor:default}
.pg:disabled{opacity:.5;cursor:default}
.gap{color:var(--muted);padding:0 2px}
@media (max-width:900px){
  th:nth-child(5),td:nth-child(5),th:nth-child(6),td:nth-child(6){display:none}
}
@media (max-width:640px){
  th:nth-child(4),td:nth-child(4){display:none}
  body{padding:14px 12px 22px}
}
</style>
</head>
<body>

<header class="top">
  <div class="brand">
    <span class="dot"></span>
    <h1>byxcr</h1>
    <span class="tag">镜像搬运箱</span>
  </div>
  <div class="actions">
    <span id="updated" class="muted"></span>
    <a class="link" href="/api" title="查看本服务的接口清单">接口清单</a>
    <a class="link" href="/api/list" title="直接查看原始 JSON">原始 JSON</a>
    <button id="refresh" class="btn" type="button">刷新</button>
  </div>
</header>

<section class="bar">
  <div class="field grow">
    <input id="q" type="search" placeholder="按镜像名筛选，如 alpine、nginx、library/" autocomplete="off" spellcheck="false">
  </div>
  <div class="field">
    <select id="status">
      <option value="">全部状态</option>
      <option value="true">仅已启用</option>
      <option value="false">仅已停用</option>
    </select>
  </div>
  <div class="field">
    <select id="size">
      <option value="10" selected>每页 10 条</option>
      <option value="20">每页 20 条</option>
      <option value="50">每页 50 条</option>
      <option value="100">每页 100 条</option>
    </select>
  </div>
  <label class="check"><input id="auto" type="checkbox"> 自动刷新 30s</label>
</section>

<div id="alert" class="alert" hidden></div>

<main class="panel">
  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th>镜像</th>
          <th>检查频率</th>
          <th>状态</th>
          <th>最近检查</th>
          <th>最近搬运</th>
          <th>镜像摘要</th>
          <th>镜像存储位置</th>
        </tr>
      </thead>
      <tbody id="rows"></tbody>
    </table>
  </div>
  <div id="empty" class="empty" hidden>没有匹配的搬运任务</div>
</main>

<footer class="foot">
  <div id="summary" class="muted">加载中 …</div>
  <nav id="pager" class="pager"></nav>
</footer>

<script>
(function () {
  'use strict';

  // 状态徽标：text 是页面上显示的字（success 与 skipped 都写「已搬运」，对用户来说都是「内容已在归档里」），
  // tip 是鼠标悬停时才显示的说明——正是靠它区分「真的重新搬了一趟」和「内容没变、跳过没搬」，
  // 失败原因同样只在悬停时给出，不再把异常文本直接铺在表格里。
  var STATUS = {
    idle:    { text: '待搬运', tip: '还没有搬运记录，等待下一次定时搬运' },
    running: { text: '搬运中', tip: '正在从镜像源下载并归档' },
    success: { text: '已搬运', tip: '重新搬运完成：上游内容有更新（或本地归档缺失），本次已下载并归档' },
    skipped: { text: '已搬运', tip: '跳过搬运：上游内容与本地归档一致，本次没有重新下载' },
    failed:  { text: '未成功', tip: '搬运未成功' }
  };
  var AUTOREFRESH_MS = 30000;

  var rowsEl = document.getElementById('rows');
  var pagerEl = document.getElementById('pager');
  var summaryEl = document.getElementById('summary');
  var updatedEl = document.getElementById('updated');
  var alertEl = document.getElementById('alert');
  var emptyEl = document.getElementById('empty');
  var qEl = document.getElementById('q');
  var statusEl = document.getElementById('status');
  var sizeEl = document.getElementById('size');
  var autoEl = document.getElementById('auto');
  var refreshEl = document.getElementById('refresh');

  var state = { q: '', status: '', size: 10, page: 1, pages: 1, total: 0, loading: false };
  var timer = null;
  var debounce = null;

  function pad(n) { return (n < 10 ? '0' : '') + n; }

  // 与 CLI 的 Duration.Format 保持一致：30d / 3h / 3m30s，零值单位省略
  function friendlyDuration(seconds) {
    var v = Math.floor(Number(seconds) || 0);
    if (v <= 0) { return '0s'; }
    var out = '';
    var days = Math.floor(v / 86400);
    if (days) { out += days + 'd'; v %= 86400; }
    var hours = Math.floor(v / 3600);
    if (hours) { out += hours + 'h'; v %= 3600; }
    var mins = Math.floor(v / 60);
    if (mins) { out += mins + 'm'; v %= 60; }
    if (v) { out += v + 's'; }
    return out;
  }

  // 服务端返回的是 UTC（ISO-8601，带 Z），这里按浏览器本地时区展示
  function localTime(value) {
    if (!value) { return ''; }
    var d = new Date(value);
    if (isNaN(d.getTime())) { return String(value); }
    return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate()) + ' ' +
           pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds());
  }

  function cell(content, className, title) {
    var td = document.createElement('td');
    if (className) { td.className = className; }
    if (title) { td.title = title; }
    if (content) { td.textContent = content; }
    return td;
  }

  function chip(label, className, tip) {
    var span = document.createElement('span');
    span.className = 'chip' + (className ? ' ' + className : '');
    span.textContent = label;
    if (tip) { span.title = tip; }
    return span;
  }

  function renderRows(tasks) {
    rowsEl.textContent = '';
    emptyEl.hidden = tasks.length > 0;

    for (var i = 0; i < tasks.length; i++) {
      var t = tasks[i];
      var tr = document.createElement('tr');

      // 镜像名（+ 自定义输出位置）
      var nameCell = document.createElement('td');
      var name = document.createElement('span');
      name.className = 'mono';
      name.textContent = t.image || '';
      nameCell.appendChild(name);
      if (t.output) {
        var outSub = document.createElement('div');
        outSub.className = 'sub';
        outSub.textContent = '输出 ' + t.output;
        nameCell.appendChild(outSub);
      }
      tr.appendChild(nameCell);

      // 检查频率（秒 → 友好格式）
      tr.appendChild(cell(friendlyDuration(t.intervalSeconds), 'dur', (t.intervalSeconds || 0) + ' 秒'));

      // 启用状态 + 最近一次搬运结果（结果徽标的细节与失败原因都放 title，悬停才显示）
      var stCell = document.createElement('td');
      stCell.appendChild(chip(t.enabled ? '启用' : '停用', t.enabled ? 'on' : '',
        t.enabled ? '已启用：按检查频率定时搬运' : '已停用：不参与定时搬运（仍可手动 sync）'));
      if (t.lastStatus) {
        var meta = STATUS[t.lastStatus] || { text: t.lastStatus, tip: '' };
        var tip = meta.tip || '';
        if (t.lastStatus === 'failed' && t.lastError) {
          tip += (tip ? '\n' : '') + '原因：' + t.lastError;
        }
        stCell.appendChild(chip(meta.text, t.lastStatus, tip));
      }
      tr.appendChild(stCell);

      tr.appendChild(cell(localTime(t.lastCheckedAt), '', t.lastCheckedAt || ''));
      tr.appendChild(cell(localTime(t.lastSuccessAt), '', t.lastSuccessAt || ''));

      // 上游摘要只展示前 12 位，完整值放 title
      var digest = t.lastDigest ? String(t.lastDigest).replace(/^sha256:/, '') : '';
      var digestCell = document.createElement('td');
      if (digest) {
        var hash = document.createElement('span');
        hash.className = 'hash';
        hash.textContent = digest.slice(0, 12);
        digestCell.title = t.lastDigest;
        digestCell.appendChild(hash);
      } else {
        digestCell.textContent = '—';
      }
      tr.appendChild(digestCell);

      var file = t.lastFile || t.output || '';
      tr.appendChild(cell(file || '—', 'file', file));

      rowsEl.appendChild(tr);
    }
  }

  function pageList(current, pages) {
    var out = [];
    var i;
    if (pages <= 7) {
      for (i = 1; i <= pages; i++) { out.push(i); }
      return out;
    }
    out.push(1);
    var start = Math.max(2, current - 1);
    var end = Math.min(pages - 1, current + 1);
    if (start > 2) { out.push(0); }
    for (i = start; i <= end; i++) { out.push(i); }
    if (end < pages - 1) { out.push(0); }
    out.push(pages);
    return out;
  }

  function renderPager() {
    pagerEl.textContent = '';
    if (state.pages <= 1) { return; }

    function button(label, page, disabled, current) {
      var b = document.createElement('button');
      b.type = 'button';
      b.className = 'pg' + (current ? ' current' : '');
      b.textContent = label;
      b.disabled = !!disabled || !!current;
      if (!b.disabled) {
        b.addEventListener('click', function () { go(page); });
      }
      pagerEl.appendChild(b);
    }

    button('‹', state.page - 1, state.page <= 1, false);
    var list = pageList(state.page, state.pages);
    for (var i = 0; i < list.length; i++) {
      if (list[i] === 0) {
        var gap = document.createElement('span');
        gap.className = 'gap';
        gap.textContent = '…';
        pagerEl.appendChild(gap);
      } else {
        button(String(list[i]), list[i], false, list[i] === state.page);
      }
    }
    button('›', state.page + 1, state.page >= state.pages, false);
  }

  function showAlert(message) {
    alertEl.hidden = !message;
    alertEl.textContent = message || '';
  }

  // 状态同步到地址栏（?q=&status=&size=&page=）：刷新/分享链接都能还原筛选与页码
  function readUrl() {
    var p = new URLSearchParams(location.search);
    var q = p.get('q');
    if (q) { state.q = q; qEl.value = q; }
    var status = p.get('status');
    if (status === 'true' || status === 'false') { state.status = status; statusEl.value = status; }
    var size = parseInt(p.get('size'), 10);
    if (size === 10 || size === 20 || size === 50 || size === 100) {
      state.size = size;
      sizeEl.value = String(size);
    }
    var page = parseInt(p.get('page'), 10);
    if (page > 0) { state.page = page; }
  }

  function writeUrl() {
    var p = new URLSearchParams();
    if (state.q) { p.set('q', state.q); }
    if (state.status) { p.set('status', state.status); }
    if (state.size !== 20) { p.set('size', String(state.size)); }
    if (state.page > 1) { p.set('page', String(state.page)); }
    var qs = p.toString();
    history.replaceState(null, '', qs ? '?' + qs : location.pathname);
  }

  function go(page) {
    state.page = page;
    load();
  }

  function load() {
    if (state.loading) { return; }
    state.loading = true;
    showAlert('');
    summaryEl.textContent = '加载中 …';

    var params = [];
    if (state.q) { params.push('filter=' + encodeURIComponent(state.q)); }
    if (state.status) { params.push('enabled=' + state.status); }
    params.push('page=' + state.page);
    params.push('limit=' + state.size);

    fetch('/api/list?' + params.join('&'), { headers: { Accept: 'application/json' }, cache: 'no-store' })
      .then(function (res) {
        return res.json().then(function (data) { return { res: res, data: data }; });
      })
      .then(function (r) {
        if (!r.res.ok || !r.data || r.data.ok !== true) {
          throw new Error((r.data && r.data.message) || ('HTTP ' + r.res.status));
        }

        var tasks = r.data.tasks || [];
        state.total = Number(r.data.total) || tasks.length;
        state.pages = Number(r.data.pageCount) || 1;
        state.page = Number(r.data.page) || 1;

        renderRows(tasks);
        renderPager();

        var text = '共 ' + state.total + ' 个任务';
        if (state.pages > 1) { text += '｜第 ' + state.page + ' / ' + state.pages + ' 页'; }
        if (state.q) { text += '｜筛选「' + state.q + '」'; }
        if (state.status) { text += state.status === 'true' ? '｜仅已启用' : '｜仅已停用'; }
        summaryEl.textContent = text;
        updatedEl.textContent = '更新于 ' + localTime(new Date().toISOString());
        writeUrl();
      })
      .catch(function (err) {
        rowsEl.textContent = '';
        emptyEl.hidden = true;
        summaryEl.textContent = '';
        showAlert('读取 /api/list 失败：' + (err && err.message ? err.message : err));
      })
      .then(function () { state.loading = false; });
  }

  qEl.addEventListener('input', function () {
    var value = this.value.trim();
    if (debounce) { clearTimeout(debounce); }
    debounce = setTimeout(function () {
      if (value === state.q) { return; }
      state.q = value;
      state.page = 1;
      load();
    }, 300);
  });

  statusEl.addEventListener('change', function () {
    state.status = this.value;
    state.page = 1;
    load();
  });

  sizeEl.addEventListener('change', function () {
    state.size = Number(this.value) || 20;
    state.page = 1;
    load();
  });

  refreshEl.addEventListener('click', function () { load(); });

  autoEl.addEventListener('change', function () {
    if (this.checked) {
      timer = setInterval(load, AUTOREFRESH_MS);
    } else if (timer) {
      clearInterval(timer);
      timer = null;
    }
  });

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && (qEl.value || state.q)) {
      qEl.value = '';
      state.q = '';
      state.page = 1;
      load();
    }
  });

  readUrl();
  load();
})();
</script>
</body>
</html>
"""";
}
