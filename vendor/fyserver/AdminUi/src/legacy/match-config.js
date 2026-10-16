import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('match-config', '对局配置');
  const $ = id => document.getElementById(id);

  function render(d) {
    const on = !!d.allGoldCards;
    $('gold-enabled').value = on ? 'true' : 'false';
    $('gold-hint').textContent = on
      ? '当前：所有牌按金卡下发'
      : '当前：按普通卡下发';
  }

  async function load() {
    const d = await Admin.api('/match-config');
    if (d && d.allGoldCards !== undefined) render(d);
    else Admin.banner(d?.message || '读取对局配置失败', 'err');
  }

  $('gold-reload').onclick = load;

  $('gold-form').onsubmit = async e => {
    e.preventDefault();
    const allGoldCards = $('gold-enabled').value === 'true';
    const r = await Admin.api('/match-config', { method: 'POST', body: { allGoldCards } });
    if (r && r.ok) {
      Admin.banner(r.message || '已保存', 'ok');
      await load();
    } else {
      Admin.banner(r?.message || '保存失败', 'err');
    }
  };

  await load();
})();
