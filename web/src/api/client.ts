import type { DeviceView, HealthView, TagView } from './types';

const KeyStorageKey = 'rung.apiKey';

/**
 * API 密钥。
 *
 * 存 sessionStorage 而不是 localStorage：关掉标签页就没了。
 * 车间里的电脑往往是多人共用的，密钥长期躺在浏览器里等于没设密钥。
 */
export const apiKey = {
  get: () => sessionStorage.getItem(KeyStorageKey) ?? '',
  set: (value: string) => {
    if (value) {
      sessionStorage.setItem(KeyStorageKey, value);
    } else {
      sessionStorage.removeItem(KeyStorageKey);
    }
  },
};

function authHeaders(): HeadersInit {
  const key = apiKey.get();
  return key ? { 'X-Rung-Key': key } : {};
}

async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(path, { signal, headers: authHeaders() });
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

  const response = await fetch(path, { method: 'POST', body, headers: authHeaders() });
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as T;
}

export interface AuditRecord {
  timestampUtc: string;
  caller: string;
  deviceId: string;
  tagName: string;
  address: string;
  dataType: string;
  requested: string;
  actual?: string | null;
  success: boolean;
  error?: string | null;
}

export const api = {
  health: (signal?: AbortSignal) => getJson<HealthView>('/api/health', signal),
  devices: (signal?: AbortSignal) => getJson<DeviceView[]>('/api/devices', signal),
  tags: (signal?: AbortSignal) => getJson<TagView[]>('/api/tags', signal),

  audit: (limit: number, signal?: AbortSignal) =>
    getJson<AuditRecord[]>(`/api/audit?limit=${limit}`, signal),

  config: (signal?: AbortSignal) => getJson<ConfigSummary>('/api/config', signal),

  /** 下载点位表。带密钥，所以不能用普通的 <a download>。 */
  async exportConfig(): Promise<Blob> {
    const response = await fetch('/api/config/export', { headers: authHeaders() });
    if (!response.ok) {
      throw new Error(await response.text());
    }

    return await response.blob();
  },

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
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ value }),
    });

    if (!response.ok) {
      throw new Error(await response.text());
    }

    return (await response.json()) as TagView;
  },
};
