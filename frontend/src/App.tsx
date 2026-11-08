import { useEffect, useMemo, useState, type FormEvent } from 'react';

type Location = 'fridge' | 'freezer' | 'pantry';
type FoodItem = { id: string; name: string; quantity: number; unit: string; location: Location; category: string; expiresAt: string | null; createdAt: string; status: 'active' | 'consumed' };
type ShoppingItem = { id: string; name: string; completed: boolean };
type Filter = 'all' | Location;

const locationNames: Record<Location, string> = { fridge: '冷藏', freezer: '冷凍', pantry: '常溫' };
const categoryNames: Record<string, string> = { other: '其他', vegetable: '蔬菜', fruit: '水果', protein: '肉蛋豆', dairy: '乳製品', staple: '主食', seasoning: '調味料' };
const shelfLife = [
  { words: ['牛奶', '鮮奶', '優格'], days: 7, category: 'dairy' },
  { words: ['雞蛋', '蛋'], days: 21, category: 'protein' },
  { words: ['青江菜', '菠菜', '青菜', '高麗菜'], days: 3, category: 'vegetable' },
  { words: ['雞肉', '豬肉', '魚', '豆腐'], days: 2, category: 'protein' },
];
const localDate = (date = new Date()) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Taipei', year: 'numeric', month: '2-digit', day: '2-digit' }).format(date);
const dayOffset = (date: string) => Math.round((Date.parse(`${date}T00:00:00+08:00`) - Date.parse(`${localDate()}T00:00:00+08:00`)) / 86400000);
function expiry(item: FoodItem) {
  if (!item.expiresAt) return { label: '正常', rank: 4, className: '' };
  const days = dayOffset(item.expiresAt);
  if (days < 0) return { label: '已過期', rank: 0, className: 'expired' };
  if (days === 0) return { label: '今天到期', rank: 1, className: 'today' };
  if (days <= 3) return { label: '3 天內到期', rank: 2, className: 'soon' };
  return { label: '正常', rank: 3, className: '' };
}

async function api<T>(path: string, code: string, method = 'GET', data?: unknown): Promise<T> {
  const response = await fetch(`/api/${path}`, {
    method,
    headers: { 'x-household-code': code, ...(data ? { 'content-type': 'application/json' } : {}) },
    ...(data ? { body: JSON.stringify(data) } : {}),
  });
  if (!response.ok) throw new Error(response.status === 401 ? '配對碼不正確' : `伺服器錯誤 ${response.status}`);
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

export default function App() {
  const [code, setCode] = useState(() => sessionStorage.getItem('fresh-days-household-code') || '');
  const [codeInput, setCodeInput] = useState(code);
  const [items, setItems] = useState<FoodItem[]>([]);
  const [shopping, setShopping] = useState<ShoppingItem[]>([]);
  const [connected, setConnected] = useState(false);
  const [message, setMessage] = useState('');
  const [filter, setFilter] = useState<Filter>('all');
  const [name, setName] = useState('');
  const [quantity, setQuantity] = useState(1);
  const [unit, setUnit] = useState('份');
  const [location, setLocation] = useState<Location>('fridge');
  const [category, setCategory] = useState('other');
  const [expiresAt, setExpiresAt] = useState('');

  useEffect(() => {
    if (!code) return;
    let active = true;
    Promise.all([api<FoodItem[]>('inventory', code), api<ShoppingItem[]>('shopping', code)])
      .then(([food, list]) => { if (active) { setItems(food); setShopping(list); setConnected(true); setMessage('已連線到家庭冰箱'); } })
      .catch((error: Error) => { if (active) { setConnected(false); setMessage(error.message); } });
    return () => { active = false; };
  }, [code]);

  async function updateItems(next: FoodItem[]) {
    try { await api('inventory', code, 'PUT', next); setItems(next); setMessage('庫存已儲存'); }
    catch (error) { setMessage((error as Error).message); }
  }
  async function updateShopping(next: ShoppingItem[]) {
    try { await api('shopping', code, 'PUT', next); setShopping(next); setMessage('採買清單已儲存'); }
    catch (error) { setMessage((error as Error).message); }
  }
  function connect(event: FormEvent) {
    event.preventDefault();
    setConnected(false);
    sessionStorage.setItem('fresh-days-household-code', codeInput.trim());
    setCode(codeInput.trim());
  }
  function addFood(event: FormEvent) {
    event.preventDefault();
    if (!name.trim() || !Number.isFinite(quantity) || quantity <= 0) return;
    const next: FoodItem = { id: crypto.randomUUID(), name: name.trim(), quantity, unit, location, category, expiresAt: expiresAt || null, createdAt: new Date().toISOString(), status: 'active' };
    void updateItems([next, ...items]);
    setName(''); setQuantity(1); setExpiresAt(''); setCategory('other');
  }
  function changeName(value: string) {
    setName(value);
    const rule = shelfLife.find((entry) => entry.words.some((word) => value.includes(word)));
    if (rule) {
      setCategory(rule.category);
      if (!expiresAt) { const date = new Date(); date.setDate(date.getDate() + rule.days); setExpiresAt(localDate(date)); }
    }
  }
  async function importOldData() {
    try {
      const oldFood = JSON.parse(localStorage.getItem('fresh-days-food-items-v1') || '[]') as FoodItem[];
      const oldShopping = JSON.parse(localStorage.getItem('fresh-days-shopping-list-v1') || '[]') as ShoppingItem[];
      if (!Array.isArray(oldFood) || !Array.isArray(oldShopping)) throw new Error('舊資料格式不正確');
      await api('inventory', code, 'PUT', oldFood);
      await api('shopping', code, 'PUT', oldShopping);
      setItems(oldFood); setShopping(oldShopping);
      setMessage('舊資料已匯入家庭冰箱');
    } catch (error) { setMessage((error as Error).message); }
  }
  const activeItems = useMemo(() => items.filter((item) => item.status !== 'consumed'), [items]);
  const visible = useMemo(() => activeItems.filter((item) => filter === 'all' || item.location === filter).sort((a, b) => expiry(a).rank - expiry(b).rank || (a.expiresAt || '9999').localeCompare(b.expiresAt || '9999')), [activeItems, filter]);
  const urgent = useMemo(() => activeItems.filter((item) => expiry(item).rank <= 2), [activeItems]);
  const recipeItems = urgent.length ? urgent : activeItems;
  const recipe = recipeItems.length ? (activeItems.some((item) => item.name.includes('蛋')) && activeItems.some((item) => item.category === 'vegetable') ? { title: '快手蔬菜烘蛋', missing: ['鹽', '食用油'] } : activeItems.some((item) => item.category === 'dairy') ? { title: '鮮奶食材濃湯', missing: ['洋蔥', '鹽'] } : { title: '冰箱清爽快炒', missing: ['蒜頭', '食用油'] }) : null;
  const missing = recipe?.missing.filter((ingredient) => !activeItems.some((item) => item.name === ingredient)) || [];

  return <main className="app-shell">
    <header className="hero"><div><p className="eyebrow">SMART FRIDGE</p><h1>鮮日子</h1><p className="muted">把冰箱裡的每一份新鮮，好好記下來。</p></div><span className="muted">{localDate()}</span></header>
    <section className="panel"><div className="section-heading"><div><p className="eyebrow">FAMILY FRIDGE</p><h2>連線家庭冰箱</h2></div></div>
      <form onSubmit={connect} className="form-grid"><label className="full">家庭配對碼<input type="password" value={codeInput} onChange={(event) => setCodeInput(event.target.value)} required autoComplete="off" /></label><button className="primary" type="submit">連線</button></form>
      <p role="status" className={connected ? 'success' : 'error'}>{message}</p>
      <small>家人在 LINE 傳送「訂閱 配對碼」即可收到每日到期通知。</small>
      {connected && items.length === 0 && localStorage.getItem('fresh-days-food-items-v1') && <p><button className="secondary" type="button" onClick={() => void importOldData()}>匯入舊版瀏覽器資料</button></p>}
    </section>
    {connected && <>
      <section className="summary"><article>目前食材<strong>{activeItems.length}</strong></article><article>需優先處理<strong>{urgent.length}</strong></article></section>
      <section className="panel"><div className="section-heading"><div><p className="eyebrow">ADD ITEM</p><h2>加入食材</h2></div></div>
        <form onSubmit={addFood} className="form-grid">
          <label>食材名稱<input value={name} onChange={(event) => changeName(event.target.value)} maxLength={60} placeholder="例如：鮮奶" required /></label>
          <label>數量<input type="number" min="0.1" step="0.1" value={quantity} onChange={(event) => setQuantity(Number(event.target.value))} required /></label>
          <label>單位<select value={unit} onChange={(event) => setUnit(event.target.value)}>{['份', '個', '包', '瓶', '盒', '公克'].map((value) => <option key={value}>{value}</option>)}</select></label>
          <label>保存位置<select value={location} onChange={(event) => setLocation(event.target.value as Location)}>{Object.entries(locationNames).map(([key, value]) => <option key={key} value={key}>{value}</option>)}</select></label>
          <label>分類<select value={category} onChange={(event) => setCategory(event.target.value)}>{Object.entries(categoryNames).map(([key, value]) => <option key={key} value={key}>{value}</option>)}</select></label>
          <label>到期日<input type="date" value={expiresAt} onChange={(event) => setExpiresAt(event.target.value)} /></label>
          <small className="full">保存期限僅供建議，請依包裝與實際狀態確認。</small><button className="primary" type="submit">加入清單</button>
        </form>
      </section>
      <section className="panel"><div className="section-heading"><div><p className="eyebrow">INVENTORY</p><h2>冰箱庫存</h2></div><div className="filters">{(['all', 'fridge', 'freezer', 'pantry'] as Filter[]).map((value) => <button type="button" key={value} className={`filter ${filter === value ? 'active' : ''}`} onClick={() => setFilter(value)}>{value === 'all' ? '全部' : locationNames[value]}</button>)}</div></div>
        {visible.length ? <ul className="list">{visible.map((item) => <li className="item" key={item.id}><div><strong>{item.name}</strong><p>{item.quantity} {item.unit} · {locationNames[item.location]} · {categoryNames[item.category] || '其他'} · {item.expiresAt || '未設定到期日'}</p></div><div className="item-actions"><span className={`status ${expiry(item).className}`}>{expiry(item).label}</span><button className="secondary" onClick={() => void updateItems(items.map((entry) => entry.id === item.id ? { ...entry, status: 'consumed' } : entry))}>已用完</button><button className="secondary" aria-label={`刪除 ${item.name}`} onClick={() => void updateItems(items.filter((entry) => entry.id !== item.id))}>刪除</button></div></li>)}</ul> : <p className="empty">還沒有食材，從上方加入第一項吧。</p>}
      </section>
      <div className="two-columns"><section className="panel"><p className="eyebrow">COOK TONIGHT</p><h2>今晚吃什麼</h2>{recipe ? <><h3>{recipe.title}</h3><p>優先使用：{recipeItems.slice(0, 3).map((item) => item.name).join('、')}</p><p>缺少材料：{missing.join('、') || '無'}</p>{missing.length > 0 && <button className="secondary" onClick={() => void updateShopping([...shopping, ...missing.filter((ingredient) => !shopping.some((item) => item.name === ingredient)).map((ingredient) => ({ id: crypto.randomUUID(), name: ingredient, completed: false }))])}>加入採買清單</button>}</> : <p className="empty">加入食材後，這裡會出現料理建議。</p>}</section>
        <section className="panel"><p className="eyebrow">SHOPPING LIST</p><h2>採買清單</h2>{shopping.length ? <ul className="list">{shopping.map((item) => <li className="item" key={item.id}><label><input className="check" type="checkbox" checked={item.completed} onChange={() => void updateShopping(shopping.map((entry) => entry.id === item.id ? { ...entry, completed: !entry.completed } : entry))} />{item.name}</label><button className="secondary" onClick={() => void updateShopping(shopping.filter((entry) => entry.id !== item.id))}>移除</button></li>)}</ul> : <p className="empty">料理缺少的材料會出現在這裡。</p>}</section></div>
    </>}
  </main>;
}
