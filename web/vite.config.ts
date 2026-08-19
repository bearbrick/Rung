import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  build: {
    // 直接输出到宿主的 wwwroot：单文件发布时前端资源跟着一起打进去，
    // 部署仍然是"拷一个可执行文件过去"
    outDir: '../src/Rung.Host/wwwroot',
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 5581,
    proxy: {
      // 开发时前端独立跑，API 与 SSE 都转发给宿主
      '/api': {
        target: 'http://127.0.0.1:5580',
        changeOrigin: true,
      },
    },
  },
});
