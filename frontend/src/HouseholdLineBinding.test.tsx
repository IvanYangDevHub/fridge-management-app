// @vitest-environment jsdom
import '@testing-library/jest-dom/vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import HouseholdApp from './HouseholdApp';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });

test('members generate a private LINE command for the household they select and can retry a failure', async () => {
  let finishHome!: (response: Response) => void;
  const pendingHome = new Promise<Response>((resolve) => { finishHome = resolve; });
  let cottageAttempts = 0;
  const fetcher = vi.fn(async (path: string) => {
    switch (path) {
      case '/api/households': return json([
        { id: 'home', name: '台中家', role: 'member' },
        { id: 'cottage', name: '台北家', role: 'member' },
      ]);
      case '/api/households/home/line-binding': return pendingHome;
      case '/api/households/cottage/line-binding':
        return cottageAttempts++ === 0 ? json({}, 503) : json({ command: '綁定 cottage-code' });
      default: return json([]);
    }
  });
  vi.stubGlobal('fetch', fetcher);
  const user = userEvent.setup();
  render(<HouseholdApp preferredHouseholdId="home" />);

  const selection = await screen.findByLabelText('目前家庭');
  const generate = () => screen.getByRole('button', { name: '產生 LINE 綁定指令' });
  expect(screen.getByText('將 LINE 綁定到「台中家」後，到期查詢與提醒都會使用這個家庭。')).toBeInTheDocument();
  await user.click(generate());
  expect(generate()).toBeDisabled();
  expect(fetcher).toHaveBeenCalledWith('/api/households/home/line-binding', { method: 'POST' });

  await user.selectOptions(selection, 'cottage');
  await act(async () => finishHome(json({ command: '綁定 private-home-code' })));
  expect(screen.queryByLabelText('LINE 綁定指令')).not.toBeInTheDocument();
  expect(screen.getByText('將 LINE 綁定到「台北家」後，到期查詢與提醒都會使用這個家庭。')).toBeInTheDocument();
  await user.click(generate());
  expect(await screen.findByRole('alert')).toHaveTextContent('無法產生 LINE 綁定指令，請再試一次。');
  await user.click(generate());
  expect(await screen.findByLabelText('LINE 綁定指令')).toHaveValue('綁定 cottage-code');
  expect(screen.getByText('請在 10 分鐘內將這段指令私訊給鮮日子 LINE 官方帳號。')).toBeInTheDocument();
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  expect(fetcher).toHaveBeenCalledWith('/api/households/cottage/line-binding', { method: 'POST' });
});
