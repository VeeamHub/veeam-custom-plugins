import React, { useEffect, useState } from 'react';
import {
    CheckboxKit,
    ComboboxKit,
    createSearchableControl,
    DIALOG_SIZE,
    Modal,
    NoteBar,
    NOTEBAR_STATUS,
    PortalSpinner,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    Text,
    TextInputKit,
    VspcDialog,
    useGlobalLang,
    useGlobalServices,
} from '@veeam-vspc/shared/components';

import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import type { PicklistValue, TicketPicklists, TicketingSettings } from 'core/apiTypes';

export interface TicketSettingsDialogProps {
    onClose: () => void;
}

// Grid items default to min-width:auto, so the kit's inputs (which carry an intrinsic
// minimum width) refuse to shrink and spill over the next column. minmax(0, 1fr) on the
// tracks plus minWidth:0 on each cell lets them shrink to the column instead of overlapping.
const fieldGridStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
    gap: 16,
    width: '100%',
};

const fieldStyle: React.CSSProperties = { display: 'flex', flexDirection: 'column', gap: 4, minWidth: 0 };

const fullWidth: React.CSSProperties = { width: '100%' };

const numberOrNull = (value: string): number | null => (value === '' ? null : Number(value));

/**
 * Ticket settings dialog, opened from the Ticketing page toolbar — the counterpart of the
 * ConnectWise Manage plugin's "Ticket Settings" action. Autotask picklists load on open so
 * previously saved selections are shown rather than appearing blank.
 */
export const TicketSettingsDialog: React.FC<TicketSettingsDialogProps> = ({ onClose }) => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const [settings, setSettings] = useState<TicketingSettings | null>(null);
    const [picklists, setPicklists] = useState<TicketPicklists | null>(null);
    const [busy, setBusy] = useState(false);

    useEffect(() => {
        pluginApi<TicketingSettings>('/api/settings/ticketing')
            .then(setSettings)
            .catch(err => notificationService.error(lang.ERROR, err.message));
        pluginApi<TicketPicklists>('/api/autotask/picklists')
            .then(setPicklists)
            .catch(() => undefined); // Autotask not connected: pickers stay empty
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const save = (): void => {
        if (!settings) return;
        setBusy(true);
        pluginApi('/api/settings/ticketing', { body: settings })
            .then(() => {
                notificationService.info(lang.SUCCESS, lang.SETTINGS_SAVED);
                onClose();
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setBusy(false));
    };

    const picklistCombo = (
        label: string,
        options: PicklistValue[] | undefined,
        value: number | null | undefined,
        onChange: (value: number | null) => void,
    ): React.ReactElement => (
        <div style={fieldStyle}>
            <Text>{label}</Text>
            <ComboboxKit
                data={options ?? []}
                value={value != null ? String(value) : null}
                valueGetter={option => option.value}
                textGetter={option => option.label}
                controlRenderer={createSearchableControl()}
                accessibility={{ isSearchable: true }}
                disabled={!options || options.length === 0}
                style={fullWidth}
                onChange={(selected: string) => onChange(numberOrNull(selected))}
            />
        </div>
    );

    const numberField = (
        label: string,
        value: number,
        fallback: number,
        apply: (next: number) => void,
    ): React.ReactElement => (
        <div style={fieldStyle}>
            <Text>{label}</Text>
            <TextInputKit
                value={String(value)}
                onlyNumbers
                style={fullWidth}
                onChange={(next: string) => apply(Number(next) || fallback)}
            />
        </div>
    );

    return (
        <Modal
            isShown
            onRequestClose={onClose}
            render={() => (
                <VspcDialog
                    header={lang.TICKET_SETTINGS_TITLE}
                    // Height auto with no max-height cap: the content is a fixed set of fields,
                    // so the dialog sizes to fit and never needs an inner scrollbar.
                    size={{ width: DIALOG_SIZE.l, height: DIALOG_SIZE.auto }}
                    movable={false}
                    loading={busy}
                    onRequestClose={onClose}
                    actions={[
                        { text: lang.SAVE, onClick: save, disabled: busy || !settings },
                        { text: lang.CANCEL, onClick: onClose, disabled: busy },
                    ]}
                >
                    {!settings
                        ? <PortalSpinner delayTime={300} />
                        : (
                            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m}>
                                {!picklists && (
                                    <NoteBar status={NOTEBAR_STATUS.info}>
                                        <Text>{lang.PICKLISTS_HINT}</Text>
                                    </NoteBar>
                                )}

                                <div style={fieldGridStyle}>
                                    {picklistCombo(lang.QUEUE, picklists?.queues, settings.queueId,
                                        v => setSettings({ ...settings, queueId: v }))}
                                    {picklistCombo(lang.NEW_TICKET_STATUS, picklists?.statuses, settings.newStatusId,
                                        v => setSettings({ ...settings, newStatusId: v }))}
                                    {picklistCombo(lang.CLOSED_TICKET_STATUS, picklists?.statuses, settings.completeStatusId,
                                        v => setSettings({ ...settings, completeStatusId: v }))}
                                    {picklistCombo(lang.WARNING_PRIORITY, picklists?.priorities, settings.warningPriorityId,
                                        v => setSettings({ ...settings, warningPriorityId: v }))}
                                    {picklistCombo(lang.ERROR_PRIORITY, picklists?.priorities, settings.errorPriorityId,
                                        v => setSettings({ ...settings, errorPriorityId: v }))}
                                    {picklistCombo(lang.SOURCE, picklists?.sources, settings.sourceId,
                                        v => setSettings({ ...settings, sourceId: v }))}
                                    {picklistCombo(lang.TICKET_TYPE, picklists?.ticketTypes, settings.ticketTypeId,
                                        v => setSettings({ ...settings, ticketTypeId: v }))}
                                    {numberField(lang.DELAY_MINUTES, settings.delayMinutes, 0,
                                        next => setSettings({ ...settings, delayMinutes: next }))}
                                    {numberField(lang.DUE_HOURS, settings.dueHours, 24,
                                        next => setSettings({ ...settings, dueHours: next }))}
                                    {numberField(lang.POLL_SECONDS, settings.pollSeconds, 120,
                                        next => setSettings({ ...settings, pollSeconds: next }))}
                                </div>

                                <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.s}>
                                    <CheckboxKit
                                        checked={settings.closeTicketOnAlarmResolve}
                                        onChange={(checked: boolean) => setSettings({ ...settings, closeTicketOnAlarmResolve: checked })}
                                    >
                                        {lang.CLOSE_TICKET_ON_RESOLVE}
                                    </CheckboxKit>
                                    <CheckboxKit
                                        checked={settings.resolveAlarmOnTicketClose}
                                        onChange={(checked: boolean) => setSettings({ ...settings, resolveAlarmOnTicketClose: checked })}
                                    >
                                        {lang.RESOLVE_ALARM_ON_CLOSE}
                                    </CheckboxKit>
                                    <CheckboxKit
                                        checked={settings.noteOnRetrigger}
                                        onChange={(checked: boolean) => setSettings({ ...settings, noteOnRetrigger: checked })}
                                    >
                                        {lang.NOTE_ON_RETRIGGER}
                                    </CheckboxKit>
                                    <CheckboxKit
                                        checked={settings.acknowledgeClosesTicket}
                                        onChange={(checked: boolean) => setSettings({ ...settings, acknowledgeClosesTicket: checked })}
                                    >
                                        {lang.ACK_CLOSES_TICKET}
                                    </CheckboxKit>
                                </StackView>
                            </StackView>
                        )}
                </VspcDialog>
            )}
        />
    );
};
