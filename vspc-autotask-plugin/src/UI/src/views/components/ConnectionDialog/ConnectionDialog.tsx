import React, { useEffect, useState } from 'react';
import {
    DIALOG_SIZE,
    Modal,
    NoteBar,
    NOTEBAR_STATUS,
    PasswordInputKit,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    Text,
    TextInputKit,
    VspcDialog,
    useGlobalLang,
} from '@veeam-vspc/shared/components';

import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import type { AutotaskSettings } from 'core/apiTypes';

export interface ConnectionDialogProps {
    onClose: () => void;
}

const fieldRowStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '1fr',
    gap: 4,
};

/**
 * Connection settings dialog, matching the ConnectWise Manage plugin's single-step
 * "Specify connection settings" dialog: field list plus Connect / Disconnect / Cancel.
 * (Feature enablement lives on the Plugin Status page, so no wizard steps are needed.)
 */
export const ConnectionDialog: React.FC<ConnectionDialogProps> = ({ onClose }) => {
    const lang = useGlobalLang<MyPluginLang>();
    const [busy, setBusy] = useState(false);
    const [existing, setExisting] = useState<AutotaskSettings | null>(null);
    const [username, setUsername] = useState('');
    const [secret, setSecret] = useState('');
    const [integrationCode, setIntegrationCode] = useState('');
    const [health, setHealth] = useState<{ ok: boolean; message: string } | null>(null);

    useEffect(() => {
        pluginApi<AutotaskSettings>('/api/settings/autotask')
            .then((settings) => {
                setExisting(settings);
                setUsername(settings.username ?? '');
                setIntegrationCode(settings.integrationCode ?? '');
            })
            .catch(() => undefined);
    }, []);

    const connect = (): void => {
        setBusy(true);
        setHealth(null);
        pluginApi<{ ok: boolean; message: string }>('/api/settings/autotask', {
            body: {
                username: username.trim(),
                secret: secret || null,
                integrationCode: integrationCode.trim(),
            },
        })
            .then((result) => {
                setHealth({ ok: true, message: `${lang.HEALTHY_STATUS} ${result.message}` });
                setTimeout(onClose, 900); // let the success note register before closing
            })
            .catch(err => setHealth({ ok: false, message: err.message }))
            .finally(() => setBusy(false));
    };

    const disconnect = (): void => {
        if (!window.confirm(lang.DISCONNECT_CONFIRMATION)) return;
        setBusy(true);
        pluginApi('/api/settings/autotask/disconnect', { body: {} })
            .then(() => onClose())
            .finally(() => setBusy(false));
    };

    return (
        <Modal
            isShown
            onRequestClose={onClose}
            render={() => (
                <VspcDialog
                    header={lang.CONNECTION_DIALOG_TITLE}
                    size={{ width: DIALOG_SIZE.s, height: DIALOG_SIZE.auto }}
                    limitDimensions
                    movable={false}
                    loading={busy}
                    onRequestClose={onClose}
                    actions={[
                        { text: lang.CONNECT, onClick: connect, disabled: busy },
                        { text: lang.DISCONNECT, onClick: disconnect, disabled: busy || !existing?.zoneUrl },
                        { text: lang.CANCEL, onClick: onClose, disabled: busy },
                    ]}
                >
                    <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m}>
                        <Text>{lang.CONNECTION_DIALOG_HINT}</Text>

                        <div style={fieldRowStyle}>
                            <Text>{lang.API_USER}</Text>
                            <TextInputKit value={username} onChange={setUsername} placeholder='api-user@example.com' />
                        </div>
                        <div style={fieldRowStyle}>
                            <Text>{lang.SECRET}</Text>
                            <PasswordInputKit
                                value={secret}
                                onChange={setSecret}
                                placeholder={existing?.hasSecret ? lang.SECRET_UNCHANGED : ''}
                            />
                        </div>
                        <div style={fieldRowStyle}>
                            <Text>{lang.INTEGRATION_CODE}</Text>
                            <TextInputKit value={integrationCode} onChange={setIntegrationCode} />
                        </div>
                        <div style={fieldRowStyle}>
                            <Text>{lang.ZONE}</Text>
                            <TextInputKit value={existing?.zoneUrl ?? ''} readOnly disabled />
                        </div>

                        <Text>{`${lang.AUTOTASK_HINT} ${lang.WIZARD_ZONE_HINT}`}</Text>

                        {health && (
                            <NoteBar status={health.ok ? NOTEBAR_STATUS.success : NOTEBAR_STATUS.error}>
                                <Text>{health.message}</Text>
                            </NoteBar>
                        )}
                    </StackView>
                </VspcDialog>
            )}
        />
    );
};
