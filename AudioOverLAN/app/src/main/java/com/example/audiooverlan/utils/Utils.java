package com.example.audiooverlan.utils;

public class Utils {
    public static Boolean isValidIPv4(String sIP){
        String ipv4Regex =
                "^((25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)\\.){3}" +
                        "(25[0-5]|2[0-4]\\d|1\\d\\d|[1-9]?\\d)$";

        return sIP.matches(ipv4Regex);
    }
}
