import type { DeviceView, HealthView, TagView } from './types';

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) {
    throw new Error(`${path} 返回 ${response.status}：${await response.text()}`);
  }
  return (await response.json()) as T;
}

export const api = {
  health: (signal?: AbortSignal) => getJson<HealthView>('/api/health', signal),
  devices: (signal?: AbortSignal) => getJson<DeviceView[]>('/api/devices', signal),
  tags: (signal?: AbortSignal) => getJson<TagView[]>('/api/tags', signal),

  /**
   * 写点位。返回的是网关从设备回读到的实际值——
   * PLC 可能对写入做钳位、取整，或被联锁逻辑改回去，界面上必须显示真正生效的值。
   */
  async write(tagName: string, value: unknown): Promise<TagView> {
    const response = await fetch(`/api/tags/${encodeURIComponent(tagName)}/write`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value }),
    });

    if (!response.ok) {
      throw new Error(await response.text());
    }

    return (await response.json()) as TagView;
  },
};
