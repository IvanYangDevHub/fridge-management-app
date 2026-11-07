const STORAGE_KEY = "fresh-days-food-items-v1";
const SHOPPING_STORAGE_KEY = "fresh-days-shopping-list-v1";

const locations = {
  fridge: { label: "冷藏", icon: "❄️" },
  freezer: { label: "冷凍", icon: "🧊" },
  pantry: { label: "常溫", icon: "🧺" },
};

const categories = {
  vegetable: "蔬菜",
  fruit: "水果",
  protein: "肉蛋豆",
  dairy: "乳製品",
  staple: "主食",
  seasoning: "調味料",
  other: "其他",
};

const shelfLifeRules = [
  { keywords: ["牛奶", "鮮奶", "優格", "豆漿"], days: 7, category: "dairy" },
  { keywords: ["雞蛋", "蛋"], days: 21, category: "protein" },
  { keywords: ["青江菜", "菠菜", "高麗菜", "青菜", "生菜", "蔬菜"], days: 3, category: "vegetable" },
  { keywords: ["雞肉", "豬肉", "牛肉", "魚", "蝦", "豆腐"], days: 2, category: "protein" },
  { keywords: ["蘋果", "香蕉", "橘子", "水果"], days: 5, category: "fruit" },
  { keywords: ["飯", "麵", "燕麥", "吐司"], days: 3, category: "staple" },
];

const form = document.querySelector("#food-form");
const list = document.querySelector("#food-list");
const emptyState = document.querySelector("#empty-state");
const count = document.querySelector("#item-count");
const expiringCount = document.querySelector("#expiring-count");
const toast = document.querySelector("#toast");
const shoppingList = document.querySelector("#shopping-list");
const shoppingEmpty = document.querySelector("#shopping-empty");
const recipeCard = document.querySelector("#recipe-card");
const shelfLifeHint = document.querySelector("#shelf-life-hint");
let activeFilter = "all";
let items = loadItems();
let shoppingItems = loadShoppingItems();

function loadItems() {
  try {
    const storedItems = JSON.parse(localStorage.getItem(STORAGE_KEY));
    return Array.isArray(storedItems)
      ? storedItems.map((item) => ({
          householdId: "local-household",
          category: "other",
          status: "active",
          consumedAt: null,
          ...item,
        }))
      : [];
  } catch {
    return [];
  }
}

function loadShoppingItems() {
  try {
    const storedItems = JSON.parse(localStorage.getItem(SHOPPING_STORAGE_KEY));
    return Array.isArray(storedItems) ? storedItems : [];
  } catch {
    return [];
  }
}

function saveItems() {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(items));
}

function saveShoppingItems() {
  localStorage.setItem(SHOPPING_STORAGE_KEY, JSON.stringify(shoppingItems));
}

function localDateString(date = new Date()) {
  const offset = date.getTimezoneOffset() * 60_000;
  return new Date(date.getTime() - offset).toISOString().slice(0, 10);
}

function daysUntil(dateString) {
  const today = new Date(`${localDateString()}T00:00:00`);
  const target = new Date(`${dateString}T00:00:00`);
  return Math.round((target - today) / 86_400_000);
}

function expiryStatus(expiresAt) {
  if (!expiresAt) return { key: "normal", label: "正常", text: "未設定到期日", priority: 4 };
  const days = daysUntil(expiresAt);
  if (days < 0) return { key: "expired", label: "已過期", text: `已過期 ${Math.abs(days)} 天`, priority: 0 };
  if (days === 0) return { key: "today", label: "今天到期", text: "今天到期", priority: 1 };
  if (days <= 3) return { key: "soon", label: "3 天內到期", text: days === 1 ? "明天到期" : `${days} 天後到期`, priority: 2 };
  return {
    key: "normal",
    label: "正常",
    text: new Intl.DateTimeFormat("zh-TW", { month: "numeric", day: "numeric" }).format(new Date(`${expiresAt}T00:00:00`)),
    priority: 3,
  };
}

function getVisibleItems() {
  return items
    .filter((item) => item.status !== "consumed")
    .filter((item) => activeFilter === "all" || item.location === activeFilter)
    .sort((a, b) => {
      const statusDifference = expiryStatus(a.expiresAt).priority - expiryStatus(b.expiresAt).priority;
      if (statusDifference !== 0) return statusDifference;
      return (a.expiresAt || "9999-12-31").localeCompare(b.expiresAt || "9999-12-31");
    });
}

function render() {
  const visibleItems = getVisibleItems();
  list.replaceChildren();
  visibleItems.forEach((item) => {
    const location = locations[item.location];
    const status = expiryStatus(item.expiresAt);
    const row = document.createElement("li");
    row.className = "food-item";
    row.innerHTML = `
      <span class="location-icon" aria-hidden="true">${location.icon}</span>
      <div><p class="food-name"></p><p class="food-meta"></p></div>
      <div class="expiry-info">
        <span class="status status-${status.key}"></span>
        <span class="expiry"></span>
      </div>
      <button class="delete-button" type="button" aria-label="刪除 ${item.name}" title="刪除">×</button>`;
    row.querySelector(".food-name").textContent = item.name;
    row.querySelector(".food-meta").textContent = `${item.quantity} ${item.unit} · ${location.label} · ${categories[item.category] || categories.other}`;
    row.querySelector(".status").textContent = status.label;
    row.querySelector(".expiry").textContent = status.text;
    row.querySelector(".delete-button").addEventListener("click", () => removeItem(item.id));
    const consumeButton = document.createElement("button");
    consumeButton.className = "consume-button";
    consumeButton.type = "button";
    consumeButton.textContent = "已用完";
    consumeButton.addEventListener("click", () => consumeItem(item.id));
    row.insertBefore(consumeButton, row.querySelector(".delete-button"));
    list.append(row);
  });
  emptyState.hidden = visibleItems.length > 0;
  const activeItems = items.filter((item) => item.status !== "consumed");
  count.textContent = activeItems.length;
  expiringCount.textContent = activeItems.filter((item) => expiryStatus(item.expiresAt).priority <= 2).length;
  renderRecipe(activeItems);
  renderShoppingList();
}

function removeItem(id) {
  const removed = items.find((item) => item.id === id);
  items = items.filter((item) => item.id !== id);
  saveItems();
  render();
  announce(`已刪除 ${removed?.name || "食材"}`);
}

function consumeItem(id) {
  const item = items.find((entry) => entry.id === id);
  if (!item) return;
  item.status = "consumed";
  item.consumedAt = new Date().toISOString();
  saveItems();
  render();
  announce(`已標記 ${item.name} 為用完`);
}

function createRecipe(activeItems) {
  const highRiskItems = activeItems
    .filter((item) => expiryStatus(item.expiresAt).priority <= 2)
    .sort((a, b) => expiryStatus(a.expiresAt).priority - expiryStatus(b.expiresAt).priority);
  const ingredients = (highRiskItems.length ? highRiskItems : activeItems).slice(0, 3);
  if (ingredients.length === 0) return null;

  const hasEgg = activeItems.some((item) => item.name.includes("蛋"));
  const hasVegetable = activeItems.some((item) => item.category === "vegetable");
  const hasDairy = activeItems.some((item) => item.category === "dairy");
  const recipe = hasEgg && hasVegetable
    ? { title: "快手蔬菜烘蛋", instruction: "把即將到期的蔬菜切小塊，和蛋一起煎熟即可。", missing: ["鹽", "食用油"] }
    : hasDairy
      ? { title: "鮮奶食材濃湯", instruction: "先炒香可用食材，加水煮軟後加入鮮奶調味。", missing: ["洋蔥", "鹽"] }
      : { title: "冰箱清爽快炒", instruction: "優先將以下食材切片快炒，做成今天的一道配菜。", missing: ["蒜頭", "食用油"] };
  const inventoryNames = new Set(activeItems.map((item) => item.name));
  return { ...recipe, ingredients, missing: recipe.missing.filter((name) => !inventoryNames.has(name)) };
}

function renderRecipe(activeItems) {
  const recipe = createRecipe(activeItems);
  if (!recipe) {
    recipeCard.innerHTML = "<h3>先加入食材吧</h3><p>有了庫存後，我會優先挑選快到期食材，整理成今晚可做的一餐。</p>";
    return;
  }
  recipeCard.replaceChildren();
  const title = document.createElement("h3");
  title.textContent = recipe.title;
  const description = document.createElement("p");
  description.append("優先消耗 ");
  const ingredients = document.createElement("strong");
  ingredients.textContent = recipe.ingredients.map((item) => item.name).join("、");
  description.append(ingredients, `。${recipe.instruction}`);
  recipeCard.append(title, description);
  if (recipe.missing.length) {
    const actions = document.createElement("div");
    actions.className = "recipe-actions";
    const button = document.createElement("button");
    button.className = "secondary-button";
    button.type = "button";
    button.textContent = `加入缺少材料：${recipe.missing.join("、")}`;
    button.addEventListener("click", () => addMissingIngredients(recipe.missing, recipe.title));
    actions.append(button);
    recipeCard.append(actions);
  }
}

function addMissingIngredients(names, sourceRecipe) {
  names.forEach((name) => {
    const existing = shoppingItems.find((item) => item.name === name && !item.completed);
    if (!existing) shoppingItems.unshift({ id: crypto.randomUUID(), name, completed: false, sourceRecipe, createdAt: new Date().toISOString() });
  });
  saveShoppingItems();
  renderShoppingList();
  announce("缺少材料已加入採買清單");
}

function renderShoppingList() {
  shoppingList.replaceChildren();
  shoppingItems.forEach((item) => {
    const row = document.createElement("li");
    row.className = `shopping-item${item.completed ? " done" : ""}`;
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = item.completed;
    checkbox.setAttribute("aria-label", `標記 ${item.name} 已購買`);
    checkbox.addEventListener("change", () => {
      item.completed = checkbox.checked;
      saveShoppingItems();
      renderShoppingList();
    });
    const name = document.createElement("span");
    name.textContent = item.name;
    const remove = document.createElement("button");
    remove.className = "remove-shopping";
    remove.type = "button";
    remove.setAttribute("aria-label", `移除 ${item.name}`);
    remove.textContent = "×";
    remove.addEventListener("click", () => {
      shoppingItems = shoppingItems.filter((entry) => entry.id !== item.id);
      saveShoppingItems();
      renderShoppingList();
    });
    row.append(checkbox, name, remove);
    shoppingList.append(row);
  });
  shoppingEmpty.hidden = shoppingItems.length > 0;
}

function announce(message) {
  toast.textContent = message;
  toast.classList.add("show");
  window.clearTimeout(announce.timeout);
  announce.timeout = window.setTimeout(() => toast.classList.remove("show"), 2200);
}

form.addEventListener("submit", (event) => {
  event.preventDefault();
  const values = new FormData(form);
  const name = values.get("name").trim();
  const quantity = Number(values.get("quantity"));
  if (!name || !Number.isFinite(quantity) || quantity <= 0) return;
  items.unshift({
    id: crypto.randomUUID(),
    name,
    quantity,
    unit: values.get("unit"),
    location: values.get("location"),
    category: values.get("category"),
    expiresAt: values.get("expiresAt") || null,
    createdAt: new Date().toISOString(),
    status: "active",
    consumedAt: null,
    householdId: "local-household",
  });
  saveItems();
  form.reset();
  form.querySelector("#quantity").value = "1";
  shelfLifeHint.textContent = "輸入食材名稱後，會提供保存期限建議。";
  render();
  announce(`已加入 ${name}`);
  form.querySelector("#name").focus();
});

function suggestShelfLife() {
  const name = form.querySelector("#name").value.trim();
  const matchedRule = shelfLifeRules.find((rule) => rule.keywords.some((keyword) => name.includes(keyword)));
  if (!name || !matchedRule) {
    shelfLifeHint.textContent = "輸入食材名稱後，會提供保存期限建議。";
    return;
  }
  shelfLifeHint.textContent = `建議：冷藏保存約 ${matchedRule.days} 天；請依包裝與實際狀態確認。`;
  form.querySelector("#category").value = matchedRule.category;
  const expiryInput = form.querySelector("#expiresAt");
  if (!expiryInput.value) {
    const suggestedDate = new Date();
    suggestedDate.setDate(suggestedDate.getDate() + matchedRule.days);
    expiryInput.value = localDateString(suggestedDate);
  }
}

form.querySelector("#name").addEventListener("input", suggestShelfLife);

document.querySelector("#line-draft-button").addEventListener("click", async () => {
  const urgentItems = items.filter((item) => item.status !== "consumed" && expiryStatus(item.expiresAt).priority <= 2);
  const lines = urgentItems.length
    ? urgentItems.map((item) => `・${item.name}：${expiryStatus(item.expiresAt).label}`).join("\n")
    : "目前沒有即將到期的食材。";
  const draft = `【鮮日子｜家庭冰箱提醒】\n${lines}\n\n今晚可優先處理冰箱裡的食材，減少浪費。`;
  try {
    await navigator.clipboard.writeText(draft);
    announce("LINE 提醒草稿已複製");
  } catch {
    announce("無法自動複製，請在安全連線的瀏覽器中再試一次");
  }
});

document.querySelectorAll(".filter").forEach((button) => {
  button.addEventListener("click", () => {
    activeFilter = button.dataset.filter;
    document.querySelectorAll(".filter").forEach((item) => item.classList.toggle("active", item === button));
    render();
  });
});

const now = new Date();
document.querySelector("#today-weekday").textContent = new Intl.DateTimeFormat("zh-TW", { weekday: "long" }).format(now);
document.querySelector("#today-date").textContent = new Intl.DateTimeFormat("zh-TW", { month: "long", day: "numeric" }).format(now);
render();
