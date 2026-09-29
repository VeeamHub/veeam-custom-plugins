import React, { useCallback, useEffect, useState } from 'react';
import {
    LinkButton,
    PortalSpinner,
    SPACE_FILL,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
    Text,
    TEXT_SIZE,
    TEXT_WEIGHT,
    ToggleKit,
    useGlobalLang,
    useGlobalServices,
} from '@veeam-vspc/shared/components';
import {
    Bell,
    BuildingApp,
    CheckmarkCircle,
    Currency,
    DismissCircle,
    ICON_SIZES,
    InfoCircle,
} from '@veeam-vspc/shared/icons';

import type { MyPluginLang } from 'configs/languages';

import { pluginApi } from 'core/pluginApi';
import { notifyFeaturesChanged } from 'core/features';
import type { FeatureToggles, StatusResponse } from 'core/apiTypes';
import { ConnectionDialog } from 'views/components/ConnectionDialog';

/** Label/value rows of the Integration Status block line up on a fixed label column. */
const statusRowStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '220px 1fr',
    gap: 8,
    alignItems: 'center',
};

// Panels are width-constrained like the reference plugin's, so the controls sit next to
// their labels instead of being pushed to the far edge of a wide browser window.
const sectionStyle: React.CSSProperties = {
    padding: '16px 24px',
    maxWidth: 860,
};

// Fixed description column keeps the toggle immediately after the text rather than letting
// a 1fr column stretch and strand the toggle on the right.
const featureRowStyle: React.CSSProperties = {
    display: 'grid',
    gridTemplateColumns: '32px minmax(0, 420px) 56px 40px',
    gap: 12,
    alignItems: 'start',
    padding: '16px 0',
    borderTop: '1px solid rgba(128,128,128,0.25)',
};

export const PluginStatusPage: React.FC = () => {
    const lang = useGlobalLang<MyPluginLang>();
    const { notificationService } = useGlobalServices();
    const [status, setStatus] = useState<StatusResponse | null>(null);
    const [featureBusy, setFeatureBusy] = useState(false);
    const [dialogOpen, setDialogOpen] = useState(false);
    const [dialogOffered, setDialogOffered] = useState(false);

    const load = useCallback((): void => {
        pluginApi<StatusResponse>('/api/status')
            .then((data) => {
                setStatus(data);
                if (!data.autotask.configured && !dialogOffered) {
                    setDialogOffered(true);
                    setDialogOpen(true);
                }
            })
            .catch(err => notificationService.error(lang.ERROR, err.message));
    }, [lang, notificationService, dialogOffered]);

    useEffect(() => load(), [load]);

    if (!status) return <PortalSpinner delayTime={300} />;

    const applyFeatures = (next: FeatureToggles): void => {
        setFeatureBusy(true);
        pluginApi('/api/settings/features', { body: next })
            .then(() => {
                setStatus(prev => (prev ? { ...prev, features: next } : prev));
                notifyFeaturesChanged(next); // navigation/tabs update immediately
            })
            .catch(err => notificationService.error(lang.ERROR, err.message))
            .finally(() => setFeatureBusy(false));
    };

    const featureRow = (
        icon: React.ReactElement,
        name: string,
        description: string,
        key: keyof FeatureToggles,
    ): React.ReactElement => (
        <div style={featureRowStyle}>
            {icon}
            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.xs}>
                <Text weight={TEXT_WEIGHT.bold}>{`${name}:`}</Text>
                <Text size={TEXT_SIZE.s}>{description}</Text>
            </StackView>
            <ToggleKit
                value={status.features[key]}
                disabled={featureBusy || !status.autotask.configured}
                onChange={(value: boolean) => applyFeatures({ ...status.features, [key]: value })}
            />
            <Text size={TEXT_SIZE.s}>{status.features[key] ? lang.ON : lang.OFF}</Text>
        </div>
    );

    const connected = status.autotask.connected;
    const statusText = connected
        ? lang.CONFIGURED
        : status.autotask.configured ? lang.CONNECTION_ERROR : lang.NOT_CONFIGURED;
    const lastUpdate = status.snapshotAge ?? status.lastTicketPoll?.at;

    return (
        // No page title here: the portal shell already renders it from the route definition.
        <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.l} spaceFill={SPACE_FILL.all} style={{ padding: 16 }}>
            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.m} style={sectionStyle}>
                <Text size={TEXT_SIZE.l} weight={TEXT_WEIGHT.bold}>{lang.INTEGRATION_STATUS}</Text>

                <div style={statusRowStyle}>
                    <Text>{lang.STATUS_LABEL}</Text>
                    <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.s} style={{ alignItems: 'center' }}>
                        {connected
                            ? <CheckmarkCircle size={ICON_SIZES.m} />
                            : status.autotask.configured
                                ? <DismissCircle size={ICON_SIZES.m} />
                                : <InfoCircle size={ICON_SIZES.m} />}
                        <Text>{statusText}</Text>
                    </StackView>
                </div>

                <div style={statusRowStyle}>
                    <Text>{lang.LAST_UPDATE}</Text>
                    <Text>{lastUpdate ? new Date(lastUpdate).toLocaleString() : '-'}</Text>
                </div>

                <div style={statusRowStyle}>
                    <Text>{lang.AUTOTASK_ZONE}</Text>
                    <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.m} style={{ alignItems: 'center' }}>
                        <Text>{status.autotask.zoneUrl || '-'}</Text>
                        <LinkButton onClick={() => setDialogOpen(true)}>{lang.CHANGE}</LinkButton>
                    </StackView>
                </div>

                {!connected && status.autotask.error && <Text size={TEXT_SIZE.s}>{status.autotask.error}</Text>}
            </StackView>

            <StackView direction={STACK_DIRECTION.column} gap={STACK_GAP.s} style={sectionStyle}>
                <Text size={TEXT_SIZE.l} weight={TEXT_WEIGHT.bold}>{lang.INTEGRATION_SETTINGS}</Text>

                <StackView direction={STACK_DIRECTION.row} gap={STACK_GAP.l} style={{ alignItems: 'center' }}>
                    <Text>{lang.ENABLE_REQUIRED_FEATURES}</Text>
                    <LinkButton
                        disabled={featureBusy || !status.autotask.configured}
                        onClick={() => applyFeatures({ companies: true, billing: true, ticketing: true })}
                    >
                        {lang.ENABLE_ALL_LINK}
                    </LinkButton>
                </StackView>

                {featureRow(<BuildingApp size={ICON_SIZES.l} />, lang.COMPANIES, lang.FEATURE_COMPANIES_DESC, 'companies')}
                {featureRow(<Bell size={ICON_SIZES.l} />, lang.TICKETING, lang.FEATURE_TICKETING_DESC, 'ticketing')}
                {featureRow(<Currency size={ICON_SIZES.l} />, lang.BILLING, lang.FEATURE_BILLING_DESC, 'billing')}

                {!status.autotask.configured && <Text size={TEXT_SIZE.s}>{lang.CONNECT_FIRST_HINT}</Text>}
            </StackView>

            {dialogOpen && (
                <ConnectionDialog
                    onClose={() => {
                        setDialogOpen(false);
                        load();
                    }}
                />
            )}
        </StackView>
    );
};
