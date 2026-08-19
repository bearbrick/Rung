import type { TagView } from '../api/types';

/** 质量对应的展示色。坏质量必须一眼看出来。 */
export function qualityColor(quality: string): string {
  switch (quality) {
    case 'Good':
      return 'green';
    case 'Stale':
      return 'orange';
    case 'Uninitialized':
      return 'default';
    default:
      return 'red';
  }
}

/** 设备状态对应的展示色。 */
export function stateColor(state: string): string {
  switch (state) {
    case 'Connected':
      return 'green';
    case 'Connecting':
      return 'blue';
    case 'Faulted':
      return 'red';
    default:
      return 'default';
  }
}

/**
 * 点位值的显示形式。
 *
 * 浮点数固定截到 3 位小数：实时表格里数字不断跳动，位数忽长忽短会让整列
 * 一直抖动，现场盯屏的人很难受。原值在 title 里保留。
 */
export function formatValue(tag: TagView): string {
  if (tag.value === null || tag.value === undefined) {
    return '—';
  }

  if (typeof tag.value === 'boolean') {
    return tag.value ? 'true' : 'false';
  }

  if (typeof tag.value === 'number') {
    return Number.isInteger(tag.value) ? String(tag.value) : tag.value.toFixed(3);
  }

  return String(tag.value);
}

/** 只显示时分秒，日期在实时视图里没有信息量。 */
export function formatTime(iso: string): string {
  const date = new Date(iso);
  return date.toLocaleTimeString('zh-CN', { hour12: false }) +
    '.' + String(date.getMilliseconds()).padStart(3, '0');
}

/** 相对当前的秒数，用于"上次成功采集"这类字段。 */
export function secondsAgo(iso: string | null | undefined): string {
  if (!iso) {
    return '—';
  }

  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) {
    return `${seconds.toFixed(0)} 秒前`;
  }

  return seconds < 3600 ? `${(seconds / 60).toFixed(0)} 分钟前` : `${(seconds / 3600).toFixed(1)} 小时前`;
}
