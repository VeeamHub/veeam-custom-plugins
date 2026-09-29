import React, { useCallback, useRef } from 'react';
import {
    BoxBorderRadius,
    BoxWidth,
    ContentBox,
    STACK_DIRECTION,
    STACK_GAP,
    StackView,
} from '@veeam-vspc/shared/components';

/**
 * The search/filter row rendered as its own surface above a grid, matching the VSPC pages
 * where the filter bar is a separate card and the action toolbar is part of the grid body.
 *
 * The controls inside cannot use the grid-bound `Search`/`BasicFilter` wrappers, because
 * those read the grid store from React context and only work as grid children. Instead the
 * page captures the grid store through Grid's `api` ref and pushes filter values in with
 * `useGridFilters`, so filtering still flows through the grid's own filter state and arrives
 * in the data callback's `requestParams.filter` exactly as before.
 */
export const GridFilterBar: React.FC<{ children: React.ReactNode }> = ({ children }) => (
    <ContentBox borderRadius={BoxBorderRadius.M} width={BoxWidth.Full}>
        <StackView
            direction={STACK_DIRECTION.row}
            gap={STACK_GAP.m}
            style={{ alignItems: 'center', flexWrap: 'wrap', width: '100%' }}
        >
            {children}
        </StackView>
    </ContentBox>
);

/** Grid store shape used by the filter bar (Grid exposes its store through the `api` ref). */
export interface GridFilterApi {
    applyFilters: (patch: Record<string, unknown>) => void;
}

/**
 * Pushes filter values into a grid captured via its `api` ref. Text input is debounced so a
 * keystroke does not trigger a fetch per character; toggles apply immediately.
 */
export function useGridFilters(gridApi: React.MutableRefObject<GridFilterApi | undefined>) {
    const timer = useRef<number>();

    const apply = useCallback((patch: Record<string, unknown>) => {
        gridApi.current?.applyFilters(patch);
    }, [gridApi]);

    const applyDebounced = useCallback((patch: Record<string, unknown>, delay = 400) => {
        window.clearTimeout(timer.current);
        timer.current = window.setTimeout(() => apply(patch), delay);
    }, [apply]);

    return { apply, applyDebounced };
}
