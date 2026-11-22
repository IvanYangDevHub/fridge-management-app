import { useState } from 'react';

export default function HouseholdLineBinding({ householdId, householdName }: {
  householdId: string;
  householdName: string;
}) {
  const [command, setCommand] = useState('');
  const [generating, setGenerating] = useState(false);
  const [failed, setFailed] = useState(false);

  async function generate() {
    setGenerating(true);
    setCommand('');
    setFailed(false);
    try {
      const response = await fetch(`/api/households/${householdId}/line-binding`, { method: 'POST' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const result: { command: string } = await response.json();
      setCommand(result.command);
    } catch {
      setFailed(true);
    } finally {
      setGenerating(false);
    }
  }

  return <section className="panel">
    <h2>LINE 到期提醒</h2>
    <p>將 LINE 綁定到「{householdName}」後，到期查詢與提醒都會使用這個家庭。</p>
    <button className="secondary" onClick={generate} disabled={generating}>產生 LINE 綁定指令</button>
    {failed && <p role="alert">無法產生 LINE 綁定指令，請再試一次。</p>}
    {command && <div>
      <p>請在 10 分鐘內將這段指令私訊給鮮日子 LINE 官方帳號。</p>
      <label>LINE 綁定指令<input value={command} readOnly /></label>
    </div>}
  </section>;
}
