import { Alert, App, Button, Card, Descriptions, Space, Table, Tag, Typography, Upload } from 'antd';
import type { UploadFile } from 'antd';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api/client';
import type { ConfigCheck, ImportResult } from '../api/client';

/**
 * 配置管理。
 *
 * 刻意不做网页版的点位表格编辑器：几百行点位在网页表格里改，体验一定不如
 * Excel，而电气工程师手上本来就是 Excel。这里支持的是真正有用的那条工作流——
 * 下载 → 在 Excel 里改 → 上传校验 → 一键生效。
 */
export function ConfigPanel() {
  const { message } = App.useApp();
  const [check, setCheck] = useState<ConfigCheck | null>(null);
  const [imported, setImported] = useState<ImportResult | null>(null);
  const [pending, setPending] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);

  const summary = useQuery({
    queryKey: ['config'],
    queryFn: ({ signal }) => api.config(signal),
  });

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    try {
      await action();
    } catch (reason) {
      message.error(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setBusy(false);
    }
  };

  const validate = (file: File) =>
    run(async () => {
      setImported(null);
      const result = await api.validateConfig(file);
      setCheck(result);
      setPending(file);

      if (result.problemCount === 0) {
        message.success(`校验通过：${result.tagCount} 个点位，每轮 ${result.requestCount} 次请求`);
      } else {
        message.warning(`发现 ${result.problemCount} 个问题，修好之后才能导入`);
      }
    });

  const apply = () =>
    run(async () => {
      if (!pending) {
        return;
      }

      const result = await api.importConfig(pending);
      setImported(result);
      setCheck(null);
      setPending(null);
      await summary.refetch();

      message.success(
        `已生效：重启 ${result.restarted.length} 台，新增 ${result.added.length} 台，` +
          `移除 ${result.removed.length} 台，${result.unchanged.length} 台未受影响`,
      );
    });

  const canApply = check !== null && check.problemCount === 0 && pending !== null;

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Card size="small" title="配置来源">
        <Descriptions size="small" column={2}>
          <Descriptions.Item label="来源">{summary.data?.source ?? '—'}</Descriptions.Item>
          <Descriptions.Item label="可在线修改">
            {summary.data?.writable ? (
              <Tag color="green">是</Tag>
            ) : (
              <Tag>否（JSON 文件来源为只读，用 --Db 指向 SQLite 才能改）</Tag>
            )}
          </Descriptions.Item>
          <Descriptions.Item label="设备">{summary.data?.deviceCount ?? '—'}</Descriptions.Item>
          <Descriptions.Item label="点位">{summary.data?.tagCount ?? '—'}</Descriptions.Item>
        </Descriptions>
      </Card>

      <Card size="small" title="点位表">
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
            下载 Excel → 在 Excel 里改 → 上传校验 → 一键生效。
            只有配置真的变了的设备会被重启，其余原地继续采集。
          </Typography.Paragraph>

          <Space wrap>
            <Button href="/api/config/export" download>
              下载点位表（Excel）
            </Button>

            <Upload
              accept=".xlsx,.json"
              maxCount={1}
              showUploadList={false}
              beforeUpload={(file: UploadFile & File) => {
                void validate(file);
                return false; // 自己发请求，不用 antd 的上传通道
              }}
            >
              <Button loading={busy}>上传并校验</Button>
            </Upload>

            <Button
              type="primary"
              disabled={!canApply || !summary.data?.writable}
              loading={busy}
              onClick={() => void apply()}
            >
              导入并生效
            </Button>

            {pending && <Typography.Text type="secondary">待导入：{pending.name}</Typography.Text>}
          </Space>
        </Space>
      </Card>

      {check && <CheckResult check={check} />}
      {imported && <ImportSummary result={imported} />}
    </Space>
  );
}

function CheckResult({ check }: { check: ConfigCheck }) {
  return (
    <Card
      size="small"
      title={
        check.problemCount === 0
          ? `校验通过：${check.tagCount} 个点位，每轮 ${check.requestCount} 次请求`
          : `发现 ${check.problemCount} 个问题`
      }
    >
      <Space direction="vertical" size={8} style={{ width: '100%' }}>
        {check.fileIssues.map((issue) => (
          <Alert key={issue} type="warning" showIcon message={issue} />
        ))}
        {check.duplicateTagNames.map((name) => (
          <Alert key={name} type="error" showIcon message={`点位名重复：${name}`} />
        ))}

        <Table
          rowKey="deviceId"
          size="small"
          pagination={false}
          dataSource={check.devices}
          columns={[
            { title: '设备', dataIndex: 'deviceId' },
            { title: '协议', dataIndex: 'protocol' },
            { title: '点位', dataIndex: 'tagCount', align: 'right' },
            {
              title: '每轮请求',
              dataIndex: 'requestCount',
              align: 'right',
              // 这两个数放一起看就知道批量合并的效果
            },
            {
              title: '问题',
              key: 'issues',
              render: (_, device) =>
                device.issues.length === 0 ? (
                  <Tag color="green">无</Tag>
                ) : (
                  <Tag color="red">{device.issues.length}</Tag>
                ),
            },
          ]}
          expandable={{
            rowExpandable: (device) => device.issues.length > 0,
            expandedRowRender: (device) => (
              <Space direction="vertical" style={{ width: '100%' }}>
                {device.issues.map((issue) => (
                  <Alert
                    key={issue.tagName + issue.reason}
                    type="warning"
                    showIcon
                    message={`${issue.tagName}：${issue.reason}`}
                  />
                ))}
              </Space>
            ),
          }}
        />
      </Space>
    </Card>
  );
}

function ImportSummary({ result }: { result: ImportResult }) {
  const rows: [string, string[], string][] = [
    ['新增', result.added, 'green'],
    ['重启', result.restarted, 'orange'],
    ['移除', result.removed, 'red'],
    ['未受影响', result.unchanged, 'default'],
  ];

  return (
    <Card size="small" title="已生效">
      <Descriptions size="small" column={1}>
        {rows.map(([label, ids, color]) => (
          <Descriptions.Item key={label} label={label}>
            {ids.length === 0 ? (
              <Typography.Text type="secondary">无</Typography.Text>
            ) : (
              ids.map((id) => (
                <Tag key={id} color={color === 'default' ? undefined : color}>
                  {id}
                </Tag>
              ))
            )}
          </Descriptions.Item>
        ))}
      </Descriptions>
    </Card>
  );
}
