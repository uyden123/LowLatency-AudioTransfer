package com.example.audiooverlan.viewmodels;

import androidx.lifecycle.LiveData;
import androidx.lifecycle.MutableLiveData;

public class PlayerStateRepository {
    private static PlayerStateRepository instance;
    private final MutableLiveData<PlayerState> playerState = new MutableLiveData<>(PlayerState.Idle.INSTANCE);

    private PlayerStateRepository() {}

    public static synchronized PlayerStateRepository getInstance() {
        if (instance == null) {
            instance = new PlayerStateRepository();
        }
        return instance;
    }

    public LiveData<PlayerState> getPlayerState() {
        return playerState;
    }

    public void updateState(PlayerState state) {
        playerState.postValue(state);
    }
}
