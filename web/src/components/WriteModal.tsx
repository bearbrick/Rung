import { App, Descriptions, Form, Input, InputNumber, Modal, Select, Typography } from 'antd';
import { useState } from 'react';
import { api } from '../api/client';
import type { TagView } from '../api/types';
import { formatValue } from './format';

interface Props {
  tag: TagView | null;
  onClose: () => void;
}

/**
 * 手动写点位。
 *
 * 现场调试时这个面板能省一半时间：不用打开博途，也不用写临时脚本。
 * 写完显示的是网关回读到的设备实际值，而不是刚填进去的值。
 */
export function WriteModal({ tag, onClose }: Props) {
  const { message } = App.useApp();
  const [value, setValue] = useState<string | number | boolean>('');
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState<TagView | null>(null);

  if (!tag) {
    return null;
  }

  const isBool = tag.dataType === 'Bool';
  const isNumeric = !isBool && tag.dataType !== 'String' && tag.dataType !== 'Bytes';

  const submit = async () => {
    setSubmitting(true);
    try {
      const written = await api.write(tag.name, value);
      setResult(written);
      message.success(`已写入 ${tag.name}，设备回读值 ${formatValue(written)}`);
    } catch (reason) {
      message.error(reason instanceof Error ? reason.message : String(reason));
    } finally {
      setSubmitting(false);
    }
  };

  const close = () => {
    setResult(null);
    setValue('');
    onClose();
  };

  return (
    <Modal
      open
      title={`写入 ${tag.name}`}
      okText="写入设备"
      cancelText="关闭"
      confirmLoading={submitting}
      onOk={submit}
      onCancel={close}
    >
      <Descriptions size="small" column={1} style={{ marginBottom: 16 }}>
        <Descriptions.Item label="地址">
          {tag.deviceId} / {tag.address}
        </Descriptions.Item>
        <Descriptions.Item label="类型">{tag.dataType}</Descriptions.Item>
        <Descriptions.Item label="当前值">{formatValue(tag)}</Descriptions.Item>
      </Descriptions>

      <Form layout="vertical">
        <Form.Item label="要写入的工程值">
          {isBool ? (
            <Select
              value={typeof value === 'boolean' ? value : undefined}
              onChange={setValue}
              options={[
                { value: true, label: 'true' },
                { value: false, label: 'false' },
              ]}
              placeholder="选择 true 或 false"
            />
          ) : isNumeric ? (
            <InputNumber
              style={{ width: '100%' }}
              value={typeof value === 'number' ? value : undefined}
              onChange={(next) => setValue(next ?? '')}
              placeholder="输入数值"
            />
          ) : (
            <Input
              value={typeof value === 'string' ? value : ''}
              onChange={(event) => setValue(event.target.value)}
            />
          )}
        </Form.Item>
      </Form>

      {result && (
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
          设备回读值：<Typography.Text strong>{formatValue(result)}</Typography.Text>
          （质量 {result.quality}）。若与填入的值不同，说明 PLC 做了钳位、取整，
          或者被联锁逻辑改回去了。
        </Typography.Paragraph>
      )}
    </Modal>
  );
}
