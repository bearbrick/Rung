import { useEffect, useRef, useState } from 'react';
import { api } from '../api/client';
import type { TagView } from '../api/types';

export type ConnectionState = 'connecting' | 'live' | 'offline';

export interface LiveTags {
  tags: Map<string, TagView>;
  /** 每个点位最近一次变化的本地时刻，用于闪烁高亮。 */
  changedAt: Map<string, number>;
  connection: ConnectionState;
  error: string | null;
}

/**
 * 点位实时视图：先用 REST 拉一次全量，之后靠 SSE 增量更新。
 *
 * 不做轮询。SSE 只推送越过死区的变化，一台设备上千个点位、500ms 一轮的情况下，
 * 轮询全量会把浏览器和网关都拖垮。
 */
export function useLiveTags(): LiveTags {
  const [tags, setTags] = useState<Map<string, TagView>>(new Map());
  const [connection, setConnection] = useState<ConnectionState>('connecting');
  const [error, setError] = useState<string | null>(null);
  const changedAt = useRef(new Map<string, number>());

  useEffect(() => {
    const controller = new AbortController();

    api
      .tags(controller.signal)
      .then((initial) => {
        setTags(new Map(initial.map((tag) => [tag.name, tag])));
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) {
          setError(reason instanceof Error ? reason.message : String(reason));
        }
      });

    // EventSource 自带断线重连，这也是选 SSE 而不是 WebSocket 的原因之一
    const source = new EventSource('/api/stream/tags');

    source.addEventListener('open', () => {
      setConnection('live');
      setError(null);
    });

    source.addEventListener('error', () => {
      setConnection(source.readyState === EventSource.CONNECTING ? 'connecting' : 'offline');
    });

    source.addEventListener('tag', (event) => {
      const tag = JSON.parse((event as MessageEvent<string>).data) as TagView;
      changedAt.current.set(tag.name, Date.now());

      setTags((previous) => {
        const next = new Map(previous);
        next.set(tag.name, tag);
        return next;
      });
    });

    return () => {
      controller.abort();
      source.close();
    };
  }, []);

  return { tags, changedAt: changedAt.current, connection, error };
}
