import type { components } from './schema';

/**
 * 全部对外类型都来自 OpenAPI 文档生成的 schema.d.ts。
 *
 * 后端改了 DTO 而前端没重新生成，`npm run lint` 会当场报错——
 * 前后端契约漂移是这类项目最常见的低级 bug 来源，一次性配好就永久消失。
 * 重新生成：先起宿主，`curl localhost:5580/openapi/v1.json -o openapi.json`，
 * 再 `npm run gen:api`。
 */
export type TagView = components['schemas']['TagView'];
export type DeviceView = components['schemas']['DeviceView'];
export type HealthView = components['schemas']['HealthView'];
export type TagIssueView = components['schemas']['TagIssueView'];
