import type { DeviceView, HealthView, TagView } from './types';

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal });
  if (!response.ok) {
    throw new Error(`${path} 返回 ${response.status}：${await response.text()}`);
  }
  return (await response.json()) as T;
}

export interface ConfigSummary {
  source: string;
  writable: boolean;
  deviceCount: number;
  tagCount: number;
}

export interface DeviceCheck {
  deviceId: string;
  protocol: string;
  tagCount: number;
  requestCount: number;
  issues: { tagName: string; reason: string }[];
}

export interface ConfigCheck {
  devices: DeviceCheck[];
  duplicateTagNames: string[];
  fileIssues: string[];
  tagCount: number;
  requestCount: number;
  problemCount: number;
}

export interface ImportResult {
  devices: DeviceCheck[];
  added: string[];
  restarted: string[];
  removed: string[];
  unchanged: string[];
}

async function postFile<T>(path: string, file: File): Promise<T> {
  const body = new FormData();
  body.append('file', file);

  const response = await fetch(path, { method: 'POST', body });
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as T;
}

export const api = {
  health: (signal?: AbortSignal) => getJson<HealthView>('/api/health', signal),
  devices: (signal?: AbortSignal) => getJson<DeviceView[]>('/api/devices', signal),
  tags: (signal?: AbortSignal) => getJson<TagView[]>('/api/tags', signal),

  config: (signal?: AbortSignal) => getJson<ConfigSummary>('/api/config', signal),

  /** 校验上传的配置，不写入任何东西。 */
  validateConfig: (file: File) => postFile<ConfigCheck>('/api/config/validate', file),

  /** 导入并立即生效。有问题会整份拒绝。 */
  importConfig: (file: File) => postFile<ImportResult>('/api/config/import', file),

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
