package com.example.audiooverlan.viewmodels;

import androidx.lifecycle.LiveData;
import androidx.lifecycle.MutableLiveData;

/**
 * Singleton repository that holds the authoritative UI state for the Audio Transmitter.
 * Services push state updates here, and ViewModels observe them.
 */
public class TransmitterStateRepository {
    private static TransmitterStateRepository instance;
    private final MutableLiveData<TransmitterState> state = new MutableLiveData<>(TransmitterState.Idle.INSTANCE);

    private TransmitterStateRepository() {}

    public static synchronized TransmitterStateRepository getInstance() {
        if (instance == null) { instance = new TransmitterStateRepository(); }
        return instance;
    }

    public LiveData<TransmitterState> getTransmitterState() {
        return state;
    }

    public void updateState(TransmitterState newState) {
        // Use postValue if called from background thread
        state.postValue(newState);
    }
}
