import { useEffect, useState } from 'react';

import { pluginApi } from './pluginApi';
import type { FeatureToggles } from './apiTypes';

/**
 * Feature toggles drive which pages exist in the UI (tabs + portal nav). Everything that
 * changes them (Dashboard toggles, the configuration wizard, the Settings page) announces
 * the new value through a window event so every mounted surface updates immediately —
 * no reload, no re-fetch race.
 */
export const FEATURES_CHANGED_EVENT = 'autotaskpsa:features-changed';

export const notifyFeaturesChanged = (next: FeatureToggles): void => {
    window.dispatchEvent(new CustomEvent<FeatureToggles>(FEATURES_CHANGED_EVENT, { detail: { ...next } }));
};

/** Current feature toggles; null while the first fetch is in flight. Live-updates on change events. */
export function useFeatureToggles(): FeatureToggles | null {
    const [features, setFeatures] = useState<FeatureToggles | null>(null);

    useEffect(() => {
        let mounted = true;
        pluginApi<FeatureToggles>('/api/settings/features')
            .then((loaded) => { if (mounted) setFeatures(loaded); })
            .catch(() => undefined); // surfaces stay in the "not loaded" state; pages handle auth errors themselves

        const onChanged = (event: Event): void => {
            const detail = (event as CustomEvent<FeatureToggles>).detail;
            if (detail) setFeatures({ ...detail });
        };
        window.addEventListener(FEATURES_CHANGED_EVENT, onChanged);
        return () => {
            mounted = false;
            window.removeEventListener(FEATURES_CHANGED_EVENT, onChanged);
        };
    }, []);

    return features;
}
